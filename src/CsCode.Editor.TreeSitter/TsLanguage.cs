using System.Runtime.InteropServices;
using CsCode.Editor.TreeSitter.Native;

namespace CsCode.Editor.TreeSitter;

/// <summary>
/// A compiled grammar: a native library exporting <c>const TSLanguage *tree_sitter_&lt;name&gt;(void)</c>. Loaded once per process and never
/// unloaded (trees and queries point into its static tables). The ABI version is checked against the runtime's accepted range before
/// anything uses it: an ABI-15 grammar on a 0.24 runtime, or an ABI-12 grammar on 0.26, is refused with a message, never parsed.
/// </summary>
public sealed unsafe class TsLanguage
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, TsLanguage> ByPath = new(StringComparer.Ordinal);

    private TsLanguage(string name, string path, IntPtr handle, uint abi)
    {
        Name = name;
        LibraryPath = path;
        Handle = handle;
        AbiVersion = abi;
    }

    /// <summary>The grammar name (<c>c_sharp</c>, <c>rust</c>, <c>markdown_inline</c>).</summary>
    public string Name { get; }

    public string LibraryPath { get; }

    public uint AbiVersion { get; }

    internal IntPtr Handle { get; }

    /// <summary>Loads (or returns the already loaded) grammar <paramref name="name"/> from <paramref name="libraryPath"/>. Needs <see cref="TsRuntime.IsAvailable"/>.</summary>
    public static TsLanguage? Load(string name, string libraryPath, out string? error)
    {
        error = null;
        if (!TsRuntime.IsAvailable)
        {
            error = TsRuntime.FailureReason ?? "the tree-sitter runtime is not loaded";
            return null;
        }
        lock (Gate)
        {
            var key = libraryPath + "|" + name;
            if (ByPath.TryGetValue(key, out var existing)) return existing;
            if (!NativeLibrary.TryLoad(libraryPath, out var lib))
            {
                error = $"could not load {libraryPath}";
                return null;
            }
            var symbol = "tree_sitter_" + name.Replace('-', '_');
            if (!NativeLibrary.TryGetExport(lib, symbol, out var fn))
            {
                error = $"{libraryPath} does not export {symbol}";
                return null;
            }
            var language = ((delegate* unmanaged[Cdecl]<IntPtr>)fn)();
            if (language == IntPtr.Zero)
            {
                error = $"{symbol} returned no language";
                return null;
            }
            var abi = TsApi.ts_language_abi_version(language);
            if (abi < TsRuntime.MinAbi || abi > TsRuntime.MaxAbi)
            {
                error = $"{name}: grammar ABI {abi} is outside the runtime's range {TsRuntime.MinAbi}-{TsRuntime.MaxAbi} ({libraryPath})";
                return null;
            }
            var loaded = new TsLanguage(name, libraryPath, language, abi);
            ByPath[key] = loaded;
            return loaded;
        }
    }

    public override string ToString() => $"{Name} (ABI {AbiVersion})";
}
