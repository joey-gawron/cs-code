#!/usr/bin/env python3
"""
lspgen: emits the committed C# protocol types for CsCode.Lsp from the official LSP 3.17 metaModel.json.

    python3 tools/lspgen/lspgen.py                      # downloads the metaModel into .scratch/lspgen/ if missing
    python3 tools/lspgen/lspgen.py --meta path.json     # use a local copy
    python3 tools/lspgen/lspgen.py --check              # exit 1 if the committed output is stale (CI / review aid)

Output (src/CsCode.Lsp/Protocol/Generated/):
  Structures.g.cs   sealed records, one per metaModel structure (extends/mixins FLATTENED) + named records for inline literals
  Enums.g.cs        integer enums -> C# enums; string enums -> open readonly record structs (unknown values round-trip)
  Methods.g.cs      method-name constants, typed LspRequest<,>/LspNotification<> descriptors, registration-option type map

Design notes (see docs/LSP.md):
  * union types become OneOf<...> (hand-written in Protocol/OneOf.cs) whose converter discriminates on JSON token kind and,
    when several arms share a token kind, on each arm's generated static Matches(JsonElement) (required props / literal values);
  * unions made only of inline literal shapes (TextDocumentFilter, TextDocumentContentChangeEvent, NotebookSelector items...)
    are merged into ONE record with all-optional differing properties;
  * LSPAny / LSPObject / LSPArray are System.Text.Json.JsonElement; URIs stay strings (mapped to System.Uri in the mappers);
  * `uinteger` and `integer` are both int (the spec bounds both to 31 bits), `decimal` is double;
  * required-but-nullable properties (processId, rootUri...) are always written, optional ones are omitted when null;
  * proposed (unstable) structures/methods are skipped.
"""
import argparse
import json
import os
import re
import sys
import urllib.request

URL = ("https://raw.githubusercontent.com/microsoft/language-server-protocol/gh-pages/_specifications/lsp/3.17/"
       "metaModel/metaModel.json")
NS = "CsCode.Lsp.Protocol"

BASE = {
    "string": "string", "integer": "int", "uinteger": "int", "decimal": "double", "boolean": "bool",
    "URI": "string", "DocumentUri": "string", "RegExp": "string", "null": None,
}
ANY_ALIASES = {"LSPAny", "LSPObject", "LSPArray"}
VALUE_TYPES = {"int", "double", "bool"}


def pascal(name):
    name = name.lstrip("$")
    return name[0].upper() + name[1:]


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def doc_summary(doc, indent):
    if not doc:
        return ""
    first = doc.strip().split("\n\n")[0]
    lines = [xml_escape(l.rstrip()) for l in first.split("\n")]
    # `code` -> <c>code</c>
    lines = [re.sub(r"`([^`]*)`", r"<c>\1</c>", l) for l in lines]
    pad = " " * indent
    out = [pad + "/// <summary>"]
    out += [pad + "/// " + l for l in lines]
    out.append(pad + "/// </summary>")
    return "\n".join(out) + "\n"


class Gen:
    def __init__(self, model):
        self.model = model
        self.structs = {s["name"]: s for s in model["structures"] if not s.get("proposed")}
        self.proposed = {s["name"] for s in model["structures"] if s.get("proposed")}
        self.enums = {e["name"]: e for e in model["enumerations"] if not e.get("proposed")}
        self.aliases = {a["name"]: a for a in model["typeAliases"]}
        self.used_names = set(self.structs) | set(self.enums)
        self.extra = []          # records emitted for inline literal / merged types: (name, props, doc)
        self.extra_names = set()
        self.flat_cache = {}
        self.alias_cache = {}

    # ---- structure flattening -------------------------------------------------------------------------------------
    def flat_props(self, name):
        if name in self.flat_cache:
            return self.flat_cache[name]
        s = self.structs[name]
        props = {}
        order = []
        for parent in list(s.get("extends", [])) + list(s.get("mixins", [])):
            if parent["kind"] != "reference" or parent["name"] not in self.structs:
                continue
            for p in self.flat_props(parent["name"]):
                if p["name"] not in props:
                    order.append(p["name"])
                props[p["name"]] = p
        for p in s["properties"]:
            if p["name"] not in props:
                order.append(p["name"])
            props[p["name"]] = p
        res = [props[n] for n in order]
        self.flat_cache[name] = res
        return res

    # ---- naming of generated helper records -----------------------------------------------------------------------
    def unique_name(self, wanted):
        name = wanted
        i = 2
        while name in self.used_names:
            name = f"{wanted}{i}"
            i += 1
        self.used_names.add(name)
        return name

    # ---- type translation -----------------------------------------------------------------------------------------
    def is_literal_union(self, t):
        return t["kind"] == "or" and len([i for i in t["items"] if i["kind"] != "base" or i.get("name") != "null"]) >= 2 and all(
            i["kind"] == "literal" or (i["kind"] == "base" and i.get("name") == "null") for i in t["items"])

    def cs(self, t, ctx):
        """Returns (csType, nullAllowed). csType never carries a trailing '?'."""
        k = t["kind"]
        if k == "base":
            b = BASE[t["name"]]
            return (b, True) if b is None else (b, False)
        if k == "reference":
            n = t["name"]
            if n in self.structs or n in self.enums:
                return n, False
            if n in ANY_ALIASES:
                return "JsonElement", False
            if n in self.aliases:
                return self.alias(n)
            return "JsonElement", False  # proposed / unknown
        if k == "array":
            el = t["element"]
            if el["kind"] == "base" and el["name"] == "uinteger":
                return "uint[]", False  # semantic token data etc.: zero-copy into Core's IReadOnlyList<uint>
            et, _ = self.cs(el, ctx + "Item")
            return f"{et}[]", False
        if k == "map":
            vt, vnull = self.cs(t["value"], ctx + "Value")
            return f"Dictionary<string, {vt}>", False
        if k == "tuple":
            return "int[]", False
        if k == "stringLiteral":
            return "string", False
        if k == "literal":
            return self.literal_record(ctx, t["value"]["properties"]), False
        if k == "or":
            items = t["items"]
            nullable = any(i["kind"] == "base" and i.get("name") == "null" for i in items)
            arms = [i for i in items if not (i["kind"] == "base" and i.get("name") == "null")]
            if self.is_literal_union(t):
                return self.merged_record(ctx, [a["value"]["properties"] for a in arms]), nullable
            types = []
            for idx, a in enumerate(arms):
                at, _ = self.cs(a, f"{ctx}Option{idx + 1}")
                if at not in types:
                    types.append(at)
            if len(types) == 1:
                return types[0], nullable
            if len(types) > 4:
                return "JsonElement", nullable
            return f"OneOf<{', '.join(types)}>", nullable
        if k == "and":
            return "JsonElement", False
        raise ValueError(f"unhandled type kind {k}")

    def alias(self, name):
        if name in self.alias_cache:
            return self.alias_cache[name]
        a = self.aliases[name]
        t = a["type"]
        if name == "DocumentFilter":
            res = self.document_filter()
        elif self.is_literal_union(t):
            res = (self.merged_record(name, [x["value"]["properties"] for x in t["items"] if x["kind"] == "literal"]), False)
        else:
            res = self.cs(t, name)
        self.alias_cache[name] = res
        return res

    def document_filter(self):
        if "DocumentFilter" in self.extra_names:
            return "DocumentFilter", False
        # TextDocumentFilter (3 literal arms) + NotebookCellTextDocumentFilter structure, merged.
        arms = [x["value"]["properties"] for x in self.aliases["TextDocumentFilter"]["type"]["items"]]
        arms.append(self.flat_props("NotebookCellTextDocumentFilter"))
        self.used_names.discard("DocumentFilter")
        return self.merged_record("DocumentFilter", arms, fixed_name=True), False

    def literal_record(self, ctx, props):
        name = self.unique_name(ctx)
        self.extra_names.add(name)
        self.extra.append((name, props, None))
        return name

    def merged_record(self, ctx, arms, fixed_name=False):
        name = ctx if fixed_name else self.unique_name(ctx)
        if fixed_name:
            self.used_names.add(name)
        if name in self.extra_names:
            return name
        self.extra_names.add(name)
        merged = {}
        order = []
        for arm in arms:
            for p in arm:
                if p["name"] not in merged:
                    merged[p["name"]] = dict(p)
                    order.append(p["name"])
        out = []
        for n in order:
            p = dict(merged[n])
            # required only if every arm requires it
            required_everywhere = all(any(q["name"] == n and not q.get("optional") for q in arm) for arm in arms)
            if not required_everywhere:
                p["optional"] = True
            out.append(p)
        self.extra.append((name, out, None))
        return name

    # ---- record emission ------------------------------------------------------------------------------------------
    def emit_record(self, name, props, doc, sb):
        sb.append(doc_summary(doc, 0))
        sb.append(f"public sealed record {name}\n{{\n")
        used = set()
        required = []  # (jsonName, literal or None)
        for p in props:
            jname = p["name"]
            pname = pascal(jname)
            if pname == name:
                pname = pname + "Value"
            while pname in used:
                pname += "_"
            used.add(pname)
            optional = bool(p.get("optional"))
            lit = p["type"]["value"] if p["type"]["kind"] == "stringLiteral" else None
            t, nullable = self.cs(p["type"], f"{name}{pascal(jname)}")
            nullable_prop = optional or nullable
            if not optional:
                required.append((jname, lit, self.kind_check(t)))
            sb.append(doc_summary(p.get("documentation"), 4))
            sb.append(f'    [JsonPropertyName("{jname}")]\n')
            if nullable and not optional:
                sb.append("    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]\n")
            is_value = (t in VALUE_TYPES or t in self.enums or t.startswith("OneOf<") or t == "JsonElement")
            if lit is not None:
                sb.append(f'    public string {pname} {{ get; init; }} = "{lit}";\n\n')
            elif nullable_prop or t == "JsonElement":
                # JsonElement is always nullable: default(JsonElement) cannot be serialized.
                sb.append(f"    public {t}? {pname} {{ get; init; }}\n\n")
            elif is_value:
                sb.append(f"    public {t} {pname} {{ get; init; }}\n\n")
            else:
                sb.append(f"    public {t} {pname} {{ get; init; }} = default!;\n\n")
        # shape matcher used by OneOf discrimination
        conds = ["e.ValueKind == JsonValueKind.Object"]
        for jname, lit, kind in required:
            v = f"p_{self.ident(jname)}"
            if lit is not None:
                conds.append(f'e.TryGetProperty("{jname}", out var {v}) && {v}.ValueKind == JsonValueKind.String && {v}.ValueEquals("{lit}")')
            elif kind:
                conds.append(f'e.TryGetProperty("{jname}", out var {v}) && {kind.format(v=v)}')
            else:
                conds.append(f'e.TryGetProperty("{jname}", out _)')
        sb.append("    /// <summary>Structural check used when a union has several object-shaped arms.</summary>\n")
        sb.append("    public static bool MatchesShape(JsonElement e) =>\n        " + "\n        && ".join(conds) + ";\n")
        sb.append("}\n\n")

    def kind_check(self, t):
        """C# condition template ({v} = the property JsonElement) checking the JSON kind of a required property, or None."""
        if t == "string" or (t in self.enums and self.enums[t]["type"]["name"] == "string"):
            return "{v}.ValueKind == JsonValueKind.String"
        if t in ("int", "double") or (t in self.enums and self.enums[t]["type"]["name"] != "string"):
            return "{v}.ValueKind == JsonValueKind.Number"
        if t == "bool":
            return "({v}.ValueKind == JsonValueKind.True || {v}.ValueKind == JsonValueKind.False)"
        if t.endswith("[]"):
            return "{v}.ValueKind == JsonValueKind.Array"
        if t.startswith("Dictionary<") or t in self.structs or t in self.extra_names:
            return "{v}.ValueKind == JsonValueKind.Object"
        return None

    @staticmethod
    def ident(s):
        return re.sub(r"\W", "_", s)

    def enums_int(self):
        return {n for n, e in self.enums.items() if e["type"]["name"] in ("integer", "uinteger")}

    def emit_enum(self, e, sb):
        name = e["name"]
        sb.append(doc_summary(e.get("documentation"), 0))
        if e["type"]["name"] in ("integer", "uinteger"):
            if name == "WatchKind":
                sb.append("[Flags]\n")
            sb.append(f"public enum {name} : int\n{{\n")
            for v in e["values"]:
                sb.append(doc_summary(v.get("documentation"), 4))
                sb.append(f"    {pascal(v['name'])} = {v['value']},\n")
            sb.append("}\n\n")
        else:
            sb.append(f"[JsonConverter(typeof(StringEnumJsonConverter<{name}>))]\n")
            sb.append(f"public readonly record struct {name}(string Value) : IStringEnum<{name}>\n{{\n")
            for v in e["values"]:
                sb.append(doc_summary(v.get("documentation"), 4))
                sb.append(f'    public static readonly {name} {pascal(v["name"])} = new("{v["value"]}");\n')
            sb.append(f"\n    public static {name} FromString(string value) => new(value);\n")
            sb.append("    public override string ToString() => Value ?? string.Empty;\n")
            sb.append("}\n\n")

    # ---- methods --------------------------------------------------------------------------------------------------
    def method_const(self, method):
        method = method.replace("$/", "Dollar/")
        return "".join(pascal(p) for p in method.split("/") if p)

    def emit_methods(self):
        sb = []
        sb.append(HEADER)
        sb.append("namespace " + NS + ";\n\n")
        reqs = [r for r in self.model["requests"] if not r.get("proposed")]
        notes = [n for n in self.model["notifications"] if not n.get("proposed")]
        sb.append("/// <summary>Every (non-proposed) LSP 3.17 method name.</summary>\npublic static class LspMethods\n{\n")
        for m in sorted(reqs + notes, key=lambda r: r["method"]):
            sb.append(f'    public const string {self.method_const(m["method"])} = "{m["method"]}";\n')
        sb.append("}\n\n")

        def result_type(r):
            t, nullable = self.cs(r["result"], "Result")
            if t is None:
                return "JsonElement?"
            return t + ("?" if nullable else "")

        def params_type(r):
            p = r.get("params")
            if p is None:
                return "NoParams"
            if p["kind"] == "reference" and p["name"] in self.structs:
                return p["name"]
            t, _ = self.cs(p, "Params")
            return t

        sb.append("/// <summary>Typed request descriptors: <c>client.RequestAsync(LspRequests.TextDocumentHover, params)</c>.</summary>\npublic static class LspRequests\n{\n")
        for r in sorted(reqs, key=lambda r: r["method"]):
            sb.append(f'    public static readonly LspRequest<{params_type(r)}, {result_type(r)}> {self.method_const(r["method"])} = new(LspMethods.{self.method_const(r["method"])});\n')
        sb.append("}\n\n")
        sb.append("public static class LspNotifications\n{\n")
        for n in sorted(notes, key=lambda r: r["method"]):
            sb.append(f'    public static readonly LspNotification<{params_type(n)}> {self.method_const(n["method"])} = new(LspMethods.{self.method_const(n["method"])});\n')
        sb.append("}\n\n")
        sb.append("/// <summary>method -> registration options type (for client/registerCapability); absent for methods without static options.</summary>\n")
        sb.append("public static class LspRegistrationOptionTypes\n{\n    public static readonly IReadOnlyDictionary<string, Type> ByMethod = new Dictionary<string, Type>\n    {\n")
        for r in sorted(reqs + notes, key=lambda r: r["method"]):
            ro = r.get("registrationOptions")
            if ro and ro["kind"] == "reference" and ro["name"] in self.structs:
                sb.append(f'        [LspMethods.{self.method_const(r["method"])}] = typeof({ro["name"]}),\n')
        sb.append("    };\n}\n")
        return "".join(sb)

    def generate(self):
        # Methods first: signatures may reference aliases/literals that need generated helper records.
        methods = self.emit_methods()
        sb = []
        sb.append(HEADER)
        sb.append("namespace " + NS + ";\n\n")
        names = sorted(self.structs)
        for n in names:
            s = self.structs[n]
            self.emit_record(n, self.flat_props(n), s.get("documentation"), sb)
        # extra records may be discovered while emitting others: loop until stable
        emitted = 0
        while emitted < len(self.extra):
            name, props, doc = self.extra[emitted]
            emitted += 1
            self.emit_record(name, props, doc, sb)
        structures = "".join(sb)

        eb = [HEADER, "namespace " + NS + ";\n\n"]
        for n in sorted(self.enums):
            self.emit_enum(self.enums[n], eb)
        enums = "".join(eb)
        return structures, enums, methods


HEADER = """// <auto-generated>
// Generated by tools/lspgen/lspgen.py from the LSP 3.17 metaModel.json. DO NOT EDIT; re-run the generator instead.
// </auto-generated>
#nullable enable
#pragma warning disable
using System.Text.Json;
using System.Text.Json.Serialization;

"""


def main():
    ap = argparse.ArgumentParser()
    root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    ap.add_argument("--meta", default=os.path.join(root, ".scratch", "lspgen", "metaModel.json"))
    ap.add_argument("--out", default=os.path.join(root, "src", "CsCode.Lsp", "Protocol", "Generated"))
    ap.add_argument("--check", action="store_true")
    a = ap.parse_args()
    if not os.path.exists(a.meta):
        os.makedirs(os.path.dirname(a.meta), exist_ok=True)
        print("downloading", URL, file=sys.stderr)
        urllib.request.urlretrieve(URL, a.meta)
    model = json.load(open(a.meta, encoding="utf-8"))
    g = Gen(model)
    structures, enums, methods = g.generate()
    files = {"Structures.g.cs": structures, "Enums.g.cs": enums, "Methods.g.cs": methods}
    os.makedirs(a.out, exist_ok=True)
    stale = False
    for name, text in files.items():
        text = text.replace("\r\n", "\n")
        path = os.path.join(a.out, name)
        if a.check:
            cur = open(path, encoding="utf-8").read() if os.path.exists(path) else None
            if cur != text:
                print("stale:", path)
                stale = True
        else:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
            print(f"wrote {path} ({text.count(chr(10))} lines)")
    if a.check and stale:
        sys.exit(1)


if __name__ == "__main__":
    main()
