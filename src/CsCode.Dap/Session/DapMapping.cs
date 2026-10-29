using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Protocol;

namespace CsCode.Dap.Session;

/// <summary>
/// Wire ⇄ Core position mapping. The client tells the adapter in <c>initialize</c> whether it counts lines/columns from 1 or 0
/// (<c>linesStartAt1</c>/<c>columnsStartAt1</c>); every position that crosses the boundary is converted here so the Core
/// models are ALWAYS 0-based and the adapter always sees the convention it was promised.
/// </summary>
public readonly record struct DapPositions(bool LinesStartAt1, bool ColumnsStartAt1)
{
    public static readonly DapPositions OneBased = new(true, true);

    private int LineOffset => LinesStartAt1 ? 1 : 0;
    private int ColumnOffset => ColumnsStartAt1 ? 1 : 0;

    public int ToCoreLine(int wire) => Math.Max(0, wire - LineOffset);
    public int ToCoreColumn(int wire) => Math.Max(0, wire - ColumnOffset);
    public int ToWireLine(int core) => core + LineOffset;
    public int ToWireColumn(int core) => core + ColumnOffset;
}

internal static class DapMapping
{
    public static DebugCapabilities ToCore(DapCapabilities c) => new(
        SupportsConfigurationDoneRequest: c.SupportsConfigurationDoneRequest == true,
        SupportsFunctionBreakpoints: c.SupportsFunctionBreakpoints == true,
        SupportsConditionalBreakpoints: c.SupportsConditionalBreakpoints == true,
        SupportsHitConditionalBreakpoints: c.SupportsHitConditionalBreakpoints == true,
        SupportsLogPoints: c.SupportsLogPoints == true,
        SupportsEvaluateForHovers: c.SupportsEvaluateForHovers == true,
        SupportsSetVariable: c.SupportsSetVariable == true,
        SupportsCompletionsRequest: c.SupportsCompletionsRequest == true,
        SupportsRestartRequest: c.SupportsRestartRequest == true,
        SupportsTerminateRequest: c.SupportsTerminateRequest == true,
        SupportsStepBack: c.SupportsStepBack == true,
        SupportsDataBreakpoints: c.SupportsDataBreakpoints == true,
        SupportsReadMemory: c.SupportsReadMemoryRequest == true,
        SupportsDisassemble: c.SupportsDisassembleRequest == true,
        SupportsExceptionInfo: c.SupportsExceptionInfoRequest == true,
        SupportsDelayedStackTraceLoading: c.SupportsDelayedStackTraceLoading == true,
        ExceptionBreakpointFilters: c.ExceptionBreakpointFilters?.Select(f =>
            new ExceptionBreakpointFilter(f.Filter, f.Label, f.Default == true, f.SupportsCondition == true, f.Description)).ToArray());

    public static DebugSource? ToCore(DapSource? s) =>
        s is null ? null : new DebugSource(s.Name, s.Path, s.SourceReference is > 0 ? s.SourceReference : null, s.PresentationHint);

    public static DebugStackFrame ToCore(DapStackFrame f, DapPositions p) => new(
        f.Id, f.Name, ToCore(f.Source), p.ToCoreLine(f.Line), p.ToCoreColumn(f.Column),
        f.EndLine is { } el ? p.ToCoreLine(el) : null,
        f.EndColumn is { } ec ? p.ToCoreColumn(ec) : null,
        f.PresentationHint,
        f.ModuleId is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } m
            ? (m.ValueKind == JsonValueKind.String ? m.GetString() : m.GetRawText())
            : null);

    public static DebugScope ToCore(DapScope s) =>
        new(s.Name, s.VariablesReference, s.Expensive, s.NamedVariables, s.IndexedVariables, s.PresentationHint);

    public static DebugVariable ToCore(DapVariable v) =>
        new(v.Name, v.Value, v.Type, v.VariablesReference, v.EvaluateName, v.NamedVariables, v.IndexedVariables, v.MemoryReference)
        {
            IsLazy = v.PresentationHint?.Lazy == true && v.VariablesReference > 0,
        };

    public static DebugEvaluateResult ToCore(DapEvaluateResponse r) =>
        new(r.Result, r.Type, r.VariablesReference, r.NamedVariables, r.IndexedVariables, r.MemoryReference);

    public static DebugThread ToCore(DapThread t) => new(t.Id, t.Name);

    public static DapSource ToWire(Uri uri)
    {
        var path = uri.IsFile ? uri.LocalPath : uri.ToString();
        return new DapSource { Path = path, Name = System.IO.Path.GetFileName(path.TrimEnd('/', '\\')) };
    }
}
