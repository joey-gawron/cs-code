using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;

namespace CsCode.Dap.Breakpoints;

// Data and instruction breakpoints (INativeBreakpointManager, lang-2). Not persisted: a data id and an instruction address belong to one
// session's debuggee. Each is sent to its own session by that session's BreakpointSync (setDataBreakpoints / setInstructionBreakpoints).
public sealed partial class BreakpointManager
{
    private readonly Emitter<object?> _nativeChanged = new();
    private DataBreakpoint[] _data = [];
    private InstructionBreakpoint[] _instructions = [];

    public IReadOnlyList<DataBreakpoint> DataBreakpoints => Volatile.Read(ref _data);
    public IReadOnlyList<InstructionBreakpoint> InstructionBreakpoints => Volatile.Read(ref _instructions);
    public IEventSource<object?> NativeChanged => _nativeChanged;

    /// <summary>Live subscribers of <see cref="NativeChanged"/> (leak tests).</summary>
    internal int NativeChangedSubscriberCount => _nativeChanged.SubscriberCount;

    private bool IsNative(string id) => _data.Any(d => d.Id == id) || _instructions.Any(i => i.Id == id);

    public DataBreakpoint AddData(string dataId, string description, string? sessionId, bool canPersist, string accessType = "write")
    {
        DataBreakpoint result;
        lock (_gate)
        {
            if (_data.FirstOrDefault(d => d.DataId == dataId && d.AccessType == accessType && d.SessionId == sessionId) is { } existing) return existing;
            result = new DataBreakpoint(NewId(), dataId, description, accessType) { CanPersist = canPersist, SessionId = sessionId };
            _data = [.. _data, result];
        }
        _nativeChanged.Fire(null);
        return result;
    }

    public InstructionBreakpoint? ToggleInstruction(string instructionReference, string? label, string? sessionId)
    {
        InstructionBreakpoint? added = null;
        lock (_gate)
        {
            var existing = _instructions.Where(i => i.SessionId == sessionId && SameAddress(i.InstructionReference, instructionReference)).ToArray();
            if (existing.Length > 0)
            {
                _instructions = [.. _instructions.Where(i => !existing.Contains(i))];
                foreach (var i in existing) _statuses.Remove(i.Id);
            }
            else
            {
                added = new InstructionBreakpoint(NewId(), instructionReference, Label: label) { SessionId = sessionId };
                _instructions = [.. _instructions, added];
            }
        }
        _nativeChanged.Fire(null);
        return added;
    }

    private static bool SameAddress(string a, string b) =>
        DebugAddress.TryParse(a, out var x) && DebugAddress.TryParse(b, out var y) ? x == y : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void SetNativeEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            if (_data.Any(d => d.Id == id && d.Enabled != enabled)) _data = [.. _data.Select(d => d.Id == id ? d with { Enabled = enabled } : d)];
            else if (_instructions.Any(i => i.Id == id && i.Enabled != enabled)) _instructions = [.. _instructions.Select(i => i.Id == id ? i with { Enabled = enabled } : i)];
            else return;
        }
        _nativeChanged.Fire(null);
    }

    public void RemoveNative(string id)
    {
        lock (_gate)
        {
            var data = _data.Where(d => d.Id != id).ToArray();
            var instructions = _instructions.Where(i => i.Id != id).ToArray();
            if (data.Length == _data.Length && instructions.Length == _instructions.Length) return;
            _data = data;
            _instructions = instructions;
            _statuses.Remove(id);
        }
        _nativeChanged.Fire(null);
    }

    public void RemoveSessionBreakpoints(string sessionId)
    {
        lock (_gate)
        {
            var data = _data.Where(d => d.SessionId != sessionId || d.CanPersist).ToArray();
            var instructions = _instructions.Where(i => i.SessionId != sessionId).ToArray();
            if (data.Length == _data.Length && instructions.Length == _instructions.Length) return;
            foreach (var gone in _data.Except(data)) _statuses.Remove(gone.Id);
            foreach (var gone in _instructions.Except(instructions)) _statuses.Remove(gone.Id);
            _data = data;
            _instructions = instructions;
        }
        _nativeChanged.Fire(null);
    }

    /// <summary>Remove All Breakpoints removes these too (VS Code's). Fires outside the caller's lock.</summary>
    private void RemoveAllNative()
    {
        lock (_gate)
        {
            if (_data.Length == 0 && _instructions.Length == 0) return;
            _data = [];
            _instructions = [];
        }
        _nativeChanged.Fire(null);
    }

    /// <summary>Enable / Disable All, under <c>_gate</c>; true when something changed (the caller fires <see cref="NativeChanged"/> after the lock).</summary>
    private bool SetAllNativeEnabled(bool enabled)
    {
        if (_data.All(d => d.Enabled == enabled) && _instructions.All(i => i.Enabled == enabled)) return false;
        _data = [.. _data.Select(d => d with { Enabled = enabled })];
        _instructions = [.. _instructions.Select(i => i with { Enabled = enabled })];
        return true;
    }
}
