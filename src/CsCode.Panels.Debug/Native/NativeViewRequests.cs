using CsCode.Core.Lifecycle;

namespace CsCode.Panels.Debug.Native;

/// <summary>Where a Disassembly or Memory window should go: an adapter memory reference (an address, a variable's or a frame's), and what to call it.</summary>
public sealed record NativeTarget(string MemoryReference, string? Label = null);

/// <summary>
/// "Open Disassembly View" (a Call Stack frame, the command), "View Binary Data" (a Locals / Watch row): requests from other windows to the
/// Disassembly and Memory windows. The windows exist only while shown, so the latest request is kept until a window takes it on creation;
/// an open window follows <see cref="DisassemblyRequested"/> / <see cref="MemoryRequested"/>. App lifetime (one per <see cref="DebugPanelServices"/>).
/// </summary>
public sealed class NativeViewRequests
{
    private readonly Emitter<NativeTarget> _disassembly = new();
    private readonly Emitter<NativeTarget> _memory = new();
    private NativeTarget? _pendingDisassembly, _pendingMemory;

    public IEventSource<NativeTarget> DisassemblyRequested => _disassembly;
    public IEventSource<NativeTarget> MemoryRequested => _memory;

    public const string DisassemblyViewId = "debug.disassembly";
    public const string MemoryViewId = "debug.memory";
    public const string RegistersViewId = "debug.registers";

    /// <summary>Brings a tool window forward by id (the shell's view activator, set by the panels' runtime); null in tests.</summary>
    internal Func<string, Task>? ShowView { get; set; }

    /// <summary>Opens the Disassembly window at <paramref name="target"/> (an open one goes there; a new one starts there).</summary>
    public Task ShowDisassembly(NativeTarget target)
    {
        Volatile.Write(ref _pendingDisassembly, target);
        _disassembly.Fire(target);
        return ShowView?.Invoke(DisassemblyViewId) ?? Task.CompletedTask;
    }

    /// <summary>Opens the Memory window at <paramref name="target"/>.</summary>
    public Task ShowMemory(NativeTarget target)
    {
        Volatile.Write(ref _pendingMemory, target);
        _memory.Fire(target);
        return ShowView?.Invoke(MemoryViewId) ?? Task.CompletedTask;
    }

    /// <summary>The request a window being created should honour (taken: a later window starts from the debugger's state instead).</summary>
    public NativeTarget? TakeDisassembly() => Interlocked.Exchange(ref _pendingDisassembly, null);

    public NativeTarget? TakeMemory() => Interlocked.Exchange(ref _pendingMemory, null);

    /// <summary>An open window handled the request itself.</summary>
    internal void Handled(NativeTarget target)
    {
        Interlocked.CompareExchange(ref _pendingDisassembly, null, target);
        Interlocked.CompareExchange(ref _pendingMemory, null, target);
    }
}
