# CsCode's loader for Zig's LLDB pretty printers (CodeLLDB and lldb-dap run it through initCommands; docs/ZIG.md).
#
# Zig ships the printers as lib/lldb/pretty_printers.py: slices as their elements with len=N, optionals as the payload or null, error unions as
# the value or the error, tagged unions as the active field, std's ArrayList/HashMap/MultiArrayList. The script uses Python 3.10 syntax (match
# statements), so it cannot load in an LLDB that embeds an older Python (Apple's LLDB, and its lldb-dap, embed 3.9): this module is written for
# Python 3.6+, checks the version first and says in the Debug Console why the values stay raw instead of failing with a SyntaxError.
import sys

import lldb


def load(debugger, path):
    if sys.version_info < (3, 10):
        print("CsCode: Zig's LLDB pretty printers need Python 3.10 or newer and this LLDB embeds Python %d.%d: slices, optionals and error "
              "unions show their raw fields. CodeLLDB (type 'lldb') embeds Python 3.12 and shows them by value." % sys.version_info[:2])
        return
    result = lldb.SBCommandReturnObject()
    quoted = '"' + path.replace("\\", "\\\\").replace('"', '\\"') + '"'
    debugger.GetCommandInterpreter().HandleCommand("command script import " + quoted, result)
    if not result.Succeeded():
        print("CsCode: Zig's LLDB pretty printers did not load (%s): %s" % (path, (result.GetError() or "").strip()))
        return
    # zig.compiler is for debugging the Zig compiler itself, not Zig programs
    for category in ("zig.lang", "zig.std"):
        debugger.HandleCommand("type category enable " + category)
