namespace CsCode.Terminal.Sessions;

/// <summary>
/// VS Code's terminal group: one entry of the terminal list (a tab), holding one terminal or several split side by side. The terminals are
/// kept in pane order (a split sits right after the terminal it was split from); <see cref="ActiveTerminal"/> is the pane that was focused
/// last in this group. Mutated only by <see cref="TerminalService"/> under its lock; readers see immutable arrays.
/// </summary>
public sealed class TerminalGroup
{
    private TerminalSession[] _terminals;
    private TerminalSession? _active;

    internal TerminalGroup(int id, TerminalSession first)
    {
        Id = id;
        _terminals = [first];
        _active = first;
    }

    /// <summary>Unique for the lifetime of the service (views key their state on it).</summary>
    public int Id { get; }

    /// <summary>The panes, left to right.</summary>
    public IReadOnlyList<TerminalSession> Terminals => Volatile.Read(ref _terminals);

    /// <summary>The pane focused last in this group (it becomes the active terminal when the group is shown).</summary>
    public TerminalSession? ActiveTerminal => Volatile.Read(ref _active);

    public bool IsSplit => Terminals.Count > 1;

    public bool Contains(TerminalSession session) => Array.IndexOf(Volatile.Read(ref _terminals), session) >= 0;

    /// <summary>"bash" for one terminal, "bash, zsh" for a split group (VS Code's group label).</summary>
    public string Title => string.Join(", ", Terminals.Select(t => t.Title));

    /// <summary>
    /// Relative widths of the panes (UI state kept with the group so a hidden and re-shown Terminal window keeps its layout); null or a
    /// different length means equal widths. Touched on the UI thread only.
    /// </summary>
    public double[]? PaneWeights { get; set; }

    internal int IndexOf(TerminalSession session) => Array.IndexOf(_terminals, session);

    internal void Insert(int index, TerminalSession session)
    {
        var list = _terminals.ToList();
        list.Insert(Math.Clamp(index, 0, list.Count), session);
        Volatile.Write(ref _terminals, list.ToArray());
        PaneWeights = null;
    }

    /// <summary>Removes a pane; the group's active pane moves to its neighbour (the one on the left, else the right).</summary>
    internal void Remove(TerminalSession session)
    {
        var index = Array.IndexOf(_terminals, session);
        if (index < 0) return;
        var list = _terminals.Where(t => !ReferenceEquals(t, session)).ToArray();
        Volatile.Write(ref _terminals, list);
        if (ReferenceEquals(_active, session))
            Volatile.Write(ref _active, list.Length == 0 ? null : list[Math.Clamp(index - 1, 0, list.Length - 1)]);
        PaneWeights = null;
    }

    internal void SetActive(TerminalSession session)
    {
        if (Contains(session)) Volatile.Write(ref _active, session);
    }

    public override string ToString() => $"group {Id}: {Title}";
}
