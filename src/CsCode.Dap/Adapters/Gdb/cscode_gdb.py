# CsCode's GDB helper, sourced before GDB's DAP interpreter starts (gdb -iex "source .../cscode_gdb.py" -i=dap).
#
# GDB's DAP shows a value without a pretty-printer that has fields (a Rust struct, enum or tuple) as an EMPTY value ("p = ", "some = ")
# whose fields are its children, and a Box as a bare address. For Rust, this adds a LAST-RESORT printer -- consulted after the Rust
# pretty-printers and every other printer, so String, Vec, HashMap, Rc... keep theirs -- whose summary is GDB's own Rust rendering of the
# value with the type paths dropped: "Point {x: 3, y: 4}", "Some(5)", "Err(\"invalid digit found in string\")", "Circle{radius: 1.5}",
# "(1, \"two\")", and "0x5555555d3eb0 Point {x: 5, y: 6}" for a pointer to one (a Box). Its children are the ones GDB's DAP shows anyway.
# Strings, slices and arrays are left to GDB. It never raises: any failure leaves the value as GDB shows it.
import gdb

_MAX_CHARS = 200
_busy = False


def _skip_literal(text, i):
    """Index just past the string or char literal that starts at text[i]."""
    quote = text[i]
    j = i + 1
    while j < len(text) and text[j] != quote:
        if text[j] == "\\":
            j += 1
        j += 1
    return j + 1


def _read_generics(text, j):
    """End (exclusive) of balanced <...> generic arguments starting at text[j], or j when they are not generics."""
    depth = 0
    k = j
    while k < len(text):
        c = text[k]
        if c == "<":
            depth += 1
        elif c == ">":
            depth -= 1
            if depth == 0:
                return k + 1
        elif c in "\"'(){}=":
            return j
        k += 1
    return j


def strip_paths(text):
    """core::option::Option<i32>::Some(5) -> Some(5); sample::Point {x: 1} -> Point {x: 1}; leaves literals alone."""
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c in "\"'":
            j = _skip_literal(text, i)
            out.append(text[i:j])
            i = j
            continue
        if c.isalpha() or c == "_":
            start = i
            last = ""
            segments = 0
            while True:
                j = i
                while j < n and (text[j].isalnum() or text[j] == "_"):
                    j += 1
                last = text[i:j]
                segments += 1
                end = _read_generics(text, j) if j < n and text[j] == "<" else j
                if text.startswith("::", end) and end + 2 < n and (text[end + 2].isalpha() or text[end + 2] == "_"):
                    i = end + 2
                    continue
                # a lone identifier stays as it is; a path is shortened to its last name (without its generic arguments)
                out.append(text[start:end] if segments == 1 else last)
                i = end
                break
            continue
        out.append(c)
        i += 1
    return "".join(out)


def _summary(value):
    global _busy
    _busy = True
    try:
        text = value.format_string(max_depth=2, max_elements=100, repeat_threshold=10, styling=False)
    except Exception:
        return ""
    finally:
        _busy = False
    text = strip_paths(text)
    return text if len(text) <= _MAX_CHARS else text[: _MAX_CHARS - 1] + "…"


class _StructSummary(gdb.ValuePrinter if hasattr(gdb, "ValuePrinter") else object):
    """A Rust struct, enum or tuple: a one-line summary, the fields as children (as GDB's no-op printer gives them)."""

    def __init__(self, ty, value):
        self.__ty = ty
        self.__value = value

    def to_string(self):
        return _summary(self.__value)

    def children(self):
        for field in self.__ty.fields():
            if hasattr(field, "bitpos") and field.name is not None:
                yield (field.name, self.__value[field])


class _PointerSummary(gdb.ValuePrinter if hasattr(gdb, "ValuePrinter") else object):
    """A pointer to a Rust struct (a Box is one in the debug info): the address and the summary of what it points to."""

    def __init__(self, value):
        self.__value = value

    def to_string(self):
        try:
            address = self.__value.format_string(raw=True, styling=False)
        except Exception:
            address = ""
        try:
            pointee = _summary(self.__value.dereference())
        except Exception:
            pointee = ""
        return (address + " " + pointee).strip() if pointee else address

    def children(self):
        yield ("value", self.__value.referenced_value())


def _lookup(value):
    if _busy:
        return None  # inside a summary: GDB's own rendering
    try:
        if gdb.current_language() != "rust":
            return None
        ty = value.type.strip_typedefs()
        if getattr(ty, "is_string_like", False) or getattr(ty, "is_array_like", False):
            return None  # &str, slices: GDB's own printers
        if ty.code in (gdb.TYPE_CODE_STRUCT, gdb.TYPE_CODE_UNION):
            return _StructSummary(ty, value)
        if ty.code == gdb.TYPE_CODE_PTR and not value.is_optimized_out and not value.is_unavailable:
            target = ty.target().strip_typedefs()
            if target.code in (gdb.TYPE_CODE_STRUCT, gdb.TYPE_CODE_UNION) and not getattr(target, "is_string_like", False) and not getattr(target, "is_array_like", False):
                return _PointerSummary(value)
    except Exception:
        return None
    return None


_lookup.__name__ = "cscode-rust-summaries"
gdb.pretty_printers.append(_lookup)
