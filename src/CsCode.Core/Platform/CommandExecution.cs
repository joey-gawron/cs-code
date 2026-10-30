namespace CsCode.Core.Platform;

/// <summary>
/// A command that ran to completion through <see cref="ICommandService.ExecuteAsync"/> (<see cref="ICommandService.Executed"/>; macro recording).
/// </summary>
/// <param name="Id">The command id as executed.</param>
/// <param name="Args">The arguments it was executed with.</param>
/// <param name="Callers">
/// Where it came from: the ids of the commands whose handlers (transitively, outermost first) executed it. Empty when nothing ran it from a command
/// handler — a key binding, a menu, a toolbar or status-bar button, or any other code outside a command; the command palette's pick has the palette
/// command (<see cref="KnownCommands.CommandPalette"/>) as its caller.
/// </param>
/// <param name="StartedTimestamp">
/// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> when the execution started (the event fires when it ends), so an observer can tell what
/// the command was started by (e.g. a key the editor was handling at that moment).
/// </param>
public sealed record CommandExecuted(string Id, IReadOnlyList<object?> Args, IReadOnlyList<string> Callers, long StartedTimestamp)
{
    /// <summary>Not executed by another command's handler.</summary>
    public bool IsTopLevel => Callers.Count == 0;
}
