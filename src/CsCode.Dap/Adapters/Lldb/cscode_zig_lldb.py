# CsCode's Zig value formatters for LLDB (CodeLLDB and lldb-dap run it through initCommands; docs/ZIG.md).
#
# Two layers, the later one winning:
# 1. Zig's own printers (lib/lldb/pretty_printers.py of the Zig installation): std's ArrayList, HashMap, MultiArrayList, Allocator. They use
#    Python 3.10 syntax (match statements), so they load only where LLDB embeds Python 3.10+ (CodeLLDB: 3.12; Apple's LLDB and its lldb-dap:
#    3.9, where this says why in the Debug Console instead of failing with a SyntaxError).
# 2. CsCode's own, in the category "cscode.zig", for the language's types: slices (len=N and the elements; u8 slices as text), optionals (the
#    payload or null), error unions (the value or the error) and tagged unions (the active field). Zig 0.17's printers read optionals as
#    {some, data} and error unions as {tag, value}, the self-hosted backend's layout; the LLVM backend (what a Debug build on macOS and Linux
#    uses) names them {payload, some} and {error, payload}, so Zig's printers show every optional as null. Ours accept both layouts and leave
#    any other shape alone. Written for Python 3.6+, so they work in every LLDB.
import sys

import lldb

CATEGORY = "cscode.zig"
MAX_CHILDREN = 2000      # a garbage len (an uninitialised slice) must not hang the Variables view
MAX_TEXT = 512           # bytes of a u8 slice shown in its summary


def load(debugger, path):
    if sys.version_info >= (3, 10):
        result = lldb.SBCommandReturnObject()
        quoted = '"' + path.replace("\\", "\\\\").replace('"', '\\"') + '"'
        debugger.GetCommandInterpreter().HandleCommand("command script import " + quoted, result)
        if result.Succeeded():
            # zig.compiler is for debugging the Zig compiler itself, not Zig programs
            for category in ("zig.lang", "zig.std"):
                debugger.HandleCommand("type category enable " + category)
        else:
            print("CsCode: Zig's LLDB pretty printers did not load (%s): %s" % (path, (result.GetError() or "").strip()))
    else:
        print("CsCode: Zig's own LLDB pretty printers (std's ArrayList, HashMap…) need Python 3.10 or newer and this LLDB embeds Python %d.%d; "
              "slices, optionals, error unions and tagged unions are still shown by value." % sys.version_info[:2])
    add_cscode_formatters(debugger)


def add_cscode_formatters(debugger):
    me = __name__
    commands = [
        # slices, sentinel-terminated too: []T, []const T, [:0]u8
        'type synthetic add --category %s --regex "^\\[(:[^]]*)?\\]" --python-class %s.SliceProvider' % (CATEGORY, me),
        'type summary add --category %s --regex "^\\[(:[^]]*)?\\]" --expand --python-function %s.slice_summary' % (CATEGORY, me),
        'type synthetic add --category %s --regex "^\\?" --python-class %s.OptionalProvider' % (CATEGORY, me),
        'type summary add --category %s --regex "^\\?" --python-function %s.optional_summary' % (CATEGORY, me),
        # error unions: E!T where E is anyerror, error{…} or a named or inferred set; pointers and arrays of them are not
        'type synthetic add --category %s --regex "^[^*?[].*!" --python-class %s.ErrorUnionProvider' % (CATEGORY, me),
        'type summary add --category %s --regex "^[^*?[].*!" --python-function %s.error_union_summary' % (CATEGORY, me),
        # tagged unions have no telling name: recognised by their shape {payload: union, tag: enum}
        'type synthetic add --category %s --recognizer-function %s.is_tagged_union --python-class %s.TaggedUnionProvider' % (CATEGORY, me, me),
        'type summary add --category %s --recognizer-function %s.is_tagged_union --python-function %s.tagged_union_summary' % (CATEGORY, me, me),
        "type category enable " + CATEGORY,  # enabled last: first in line, before zig.lang
    ]
    for command in commands:
        result = lldb.SBCommandReturnObject()
        debugger.GetCommandInterpreter().HandleCommand(command, result)
        if not result.Succeeded() and "recognizer" not in command:  # an LLDB older than 16 has no recognizer functions: tagged unions stay raw
            print("CsCode: %s failed: %s" % (command, (result.GetError() or "").strip()))


def _member(value, *names):
    for name in names:
        child = value.GetChildMemberWithName(name)
        if child.IsValid():
            return child
    return None


def _show(value, depth=0):
    if value is None or not value.IsValid():
        return "?"
    summary = value.GetSummary()
    if summary:
        return summary
    text = value.GetValue()
    if text is not None:
        return text
    count = value.GetNumChildren()
    if count == 0:
        return value.GetTypeName()
    if count > 4 or depth > 0:
        return "{...}"
    # a small struct inline, as LLDB's own one-line summaries are: {w = 3, h = 4}
    return "{%s}" % ", ".join("%s = %s" % (c.GetName(), _show(c, depth + 1)) for c in (value.GetChildAtIndex(i) for i in range(count)))


class _PassThrough(object):
    """A provider for a value of an unexpected shape: its own children, unchanged."""

    def __init__(self, value):
        self.value = value

    def num_children(self):
        return self.value.GetNumChildren()

    def get_child_index(self, name):
        return self.value.GetIndexOfChildWithName(name)

    def get_child_at_index(self, index):
        return self.value.GetChildAtIndex(index)

    def update(self):
        return False

    def has_children(self):
        return self.value.GetNumChildren() > 0


# ------------------------------------------------------------------ slices

def _slice_parts(value):
    ptr = _member(value, "ptr")
    length = _member(value, "len")
    if ptr is None or length is None or not ptr.GetType().IsPointerType():
        return None
    return ptr, length.GetValueAsUnsigned(0)


class SliceProvider(object):
    def __init__(self, value, internal_dict=None):
        self.value = value
        self.update()

    def update(self):
        parts = _slice_parts(self.value)
        self.ptr, self.length = (parts if parts else (None, 0))
        self.fallback = None if parts else _PassThrough(self.value)
        if parts:
            self.element = self.ptr.GetType().GetPointeeType()
            self.size = self.element.GetByteSize()
            self.address = self.ptr.GetValueAsUnsigned(0)
        return False

    def has_children(self):
        return self.fallback.has_children() if self.fallback else self.length > 0

    def num_children(self):
        return self.fallback.num_children() if self.fallback else min(self.length, MAX_CHILDREN)

    def get_child_index(self, name):
        if self.fallback:
            return self.fallback.get_child_index(name)
        try:
            return int(name.strip("[]"))
        except ValueError:
            return -1

    def get_child_at_index(self, index):
        if self.fallback:
            return self.fallback.get_child_at_index(index)
        if index < 0 or index >= self.num_children() or self.size == 0:
            return None
        return self.value.CreateValueFromAddress("[%d]" % index, self.address + index * self.size, self.element)


def slice_summary(value, internal_dict=None):
    raw = value.GetNonSyntheticValue()
    parts = _slice_parts(raw)
    if not parts:
        return None
    ptr, length = parts
    element = ptr.GetType().GetPointeeType()
    if element.GetUnqualifiedType().GetName() in ("u8", "unsigned char", "char") and element.GetByteSize() == 1:
        text = _read_text(value, ptr.GetValueAsUnsigned(0), length)
        if text is not None:
            return text
    return "len=%d" % length


def _read_text(value, address, length):
    process = value.GetProcess()
    if not process.IsValid() or address == 0:
        return '""' if length == 0 else None
    error = lldb.SBError()
    data = process.ReadMemory(address, min(length, MAX_TEXT), error) if length else b""
    if not error.Success():
        return None
    text = data.decode("utf-8", "replace").replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t")
    return '"%s"%s' % (text, "…" if length > MAX_TEXT else "")


# ------------------------------------------------------------------ optionals

def _optional_payload(value):
    """(is_layout, payload or None): the payload when the optional holds one; (False, None) for a shape this does not know."""
    if value.GetType().IsPointerType():  # ?*T is a nullable pointer
        return True, (value if value.GetValueAsUnsigned(0) != 0 else None)
    some = _member(value, "some")
    payload = _member(value, "payload", "data")
    if some is None or payload is None:
        return False, None
    return True, (payload if some.GetValueAsUnsigned(0) != 0 else None)


class OptionalProvider(object):
    def __init__(self, value, internal_dict=None):
        self.value = value
        self.update()

    def update(self):
        known, self.payload = _optional_payload(self.value)
        self.fallback = None if known else _PassThrough(self.value)
        self.pointer = known and self.value.GetType().IsPointerType()
        return False

    def _target(self):
        # a non-null ?*T expands to what it points at; any other payload to its own children
        if self.payload is None:
            return None
        return self.payload.Dereference() if self.pointer else self.payload

    def has_children(self):
        if self.fallback:
            return self.fallback.has_children()
        target = self._target()
        return target is not None and target.GetNumChildren() > 0

    def num_children(self):
        if self.fallback:
            return self.fallback.num_children()
        target = self._target()
        return target.GetNumChildren() if target is not None else 0

    def get_child_index(self, name):
        if self.fallback:
            return self.fallback.get_child_index(name)
        target = self._target()
        return target.GetIndexOfChildWithName(name) if target is not None else -1

    def get_child_at_index(self, index):
        if self.fallback:
            return self.fallback.get_child_at_index(index)
        target = self._target()
        return target.GetChildAtIndex(index) if target is not None else None


def optional_summary(value, internal_dict=None):
    known, payload = _optional_payload(value.GetNonSyntheticValue())
    if not known:
        return None
    if payload is None:
        return "null"
    return payload.GetValue() if value.GetType().IsPointerType() else _show(payload)


# ------------------------------------------------------------------ error unions

def _error_union_parts(value):
    """(error, payload) with error None when there is none; None for a shape this does not know."""
    error = _member(value, "error", "tag")
    payload = _member(value, "payload", "value")
    if error is None or payload is None:
        return None
    return (error if error.GetValueAsUnsigned(0) != 0 else None), payload


class ErrorUnionProvider(object):
    def __init__(self, value, internal_dict=None):
        self.value = value
        self.update()

    def update(self):
        parts = _error_union_parts(self.value)
        self.fallback = None if parts else _PassThrough(self.value)
        self.child = None
        if parts:
            error, payload = parts
            self.child = error.Clone("error") if error is not None else payload.Clone("value")
        return False

    def has_children(self):
        return self.fallback.has_children() if self.fallback else True

    def num_children(self):
        return self.fallback.num_children() if self.fallback else 1

    def get_child_index(self, name):
        if self.fallback:
            return self.fallback.get_child_index(name)
        return 0 if self.child is not None and name == self.child.GetName() else -1

    def get_child_at_index(self, index):
        if self.fallback:
            return self.fallback.get_child_at_index(index)
        return self.child if index == 0 else None


def error_union_summary(value, internal_dict=None):
    parts = _error_union_parts(value.GetNonSyntheticValue())
    if not parts:
        return None
    error, payload = parts
    if error is not None:
        name = error.GetValue() or str(error.GetValueAsUnsigned(0))
        return name if name.startswith("error.") else "error." + name
    return _show(payload)


# ------------------------------------------------------------------ tagged unions

def is_tagged_union(sbtype, internal_dict=None):
    if sbtype.GetTypeClass() not in (lldb.eTypeClassStruct, lldb.eTypeClassClass) or sbtype.GetNumberOfFields() != 2:
        return False
    fields = dict((sbtype.GetFieldAtIndex(i).GetName(), sbtype.GetFieldAtIndex(i).GetType()) for i in range(2))
    return ("payload" in fields and "tag" in fields
            and fields["payload"].GetCanonicalType().GetTypeClass() == lldb.eTypeClassUnion
            and fields["tag"].GetCanonicalType().GetTypeClass() == lldb.eTypeClassEnumeration)


def _active(value):
    tag = _member(value, "tag")
    payload = _member(value, "payload")
    if tag is None or payload is None:
        return None, None
    name = tag.GetValue()
    field = payload.GetChildMemberWithName(name) if name else None
    return name, (field if field is not None and field.IsValid() else None)


class TaggedUnionProvider(object):
    def __init__(self, value, internal_dict=None):
        self.value = value
        self.update()

    def update(self):
        self.name, self.field = _active(self.value)
        return False

    def has_children(self):
        return True

    def num_children(self):
        return 1

    def get_child_index(self, name):
        return 0 if name == (self.name or "tag") else -1

    def get_child_at_index(self, index):
        if index != 0:
            return None
        if self.field is not None:
            return self.field
        return _member(self.value, "tag")  # a field without payload (void): the tag alone


def tagged_union_summary(value, internal_dict=None):
    name, field = _active(value.GetNonSyntheticValue())
    if name is None:
        return None
    if field is None or field.GetType().GetByteSize() == 0:
        return "." + name
    return ".%s = %s" % (name, _show(field))
