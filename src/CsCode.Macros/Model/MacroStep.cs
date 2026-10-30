using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Macros;

public enum MacroStepKind { Key, Text, Command, Pause }

/// <summary>
/// One recorded step of a macro (immutable). A key chord, menu item or palette pick that ran a command is recorded as the <see cref="CommandStep"/>
/// itself (deterministic replay, independent of key bindings); <see cref="KeyStep"/> is a key the editor handled itself (no binding:
/// arrows, Home, Backspace, Enter...); <see cref="TextStep"/> is typed or IME text; <see cref="PauseStep"/> waits (recorded only with
/// <c>macros.recordPauses</c>, or written by hand).
/// </summary>
public abstract record MacroStep
{
    public abstract MacroStepKind Kind { get; }

    /// <summary>Whether replaying the step types into the editor (key and text steps).</summary>
    public bool TypesIntoEditor => Kind is MacroStepKind.Key or MacroStepKind.Text;
}

/// <summary>A key press the editor handled itself (no key binding resolved it when it was recorded).</summary>
public sealed record KeyStep(KeyStroke Key) : MacroStep
{
    public override MacroStepKind Kind => MacroStepKind.Key;
    public override string ToString() => "key " + Key;
}

/// <summary>Typed text (consecutive characters are merged into one step; replay types them one by one).</summary>
public sealed record TextStep(string Text) : MacroStep
{
    public override MacroStepKind Kind => MacroStepKind.Text;
    public override string ToString() => "text " + JsonSerializer.Serialize(Text);
}

/// <summary>A command with JSON arguments (what a key binding, menu item or palette pick ran).</summary>
public sealed record CommandStep(string Command, IReadOnlyList<JsonElement> Args) : MacroStep
{
    public CommandStep(string command) : this(command, []) { }

    public override MacroStepKind Kind => MacroStepKind.Command;

    /// <summary>The arguments as passed to <see cref="ICommandService.ExecuteAsync"/>.</summary>
    public object?[] ArgumentArray()
    {
        if (Args.Count == 0) return [];
        var a = new object?[Args.Count];
        for (var i = 0; i < a.Length; i++) a[i] = Args[i].ValueKind == JsonValueKind.Null ? null : Args[i];
        return a;
    }

    public bool Equals(CommandStep? other) =>
        other is not null && Command == other.Command && Args.Count == other.Args.Count && Args.Zip(other.Args).All(p => JsonElement.DeepEquals(p.First, p.Second));

    public override int GetHashCode() => HashCode.Combine(Command, Args.Count);

    public override string ToString() => Args.Count == 0 ? "command " + Command : $"command {Command} {JsonSerializer.Serialize(Args)}";
}

/// <summary>Waits <see cref="Milliseconds"/> before the next step (the GUI keeps running).</summary>
public sealed record PauseStep(int Milliseconds) : MacroStep
{
    public const int MaxMilliseconds = 60_000;
    public override MacroStepKind Kind => MacroStepKind.Pause;
    public override string ToString() => $"pause {Milliseconds} ms";
}

/// <summary>
/// A macro: a name (empty for the last recorded, unsaved one), its steps and, for saved macros, a description and an optional key binding.
/// Saved macros run as the command <c>macro.run.&lt;name&gt;</c>.
/// </summary>
public sealed record Macro(string Name, IReadOnlyList<MacroStep> Steps)
{
    public string? Description { get; init; }

    /// <summary>A key binding for <see cref="CommandId"/> in VS Code syntax ("ctrl+alt+m", "ctrl+k m"), contributed below keybindings.json.</summary>
    public string? Key { get; init; }

    public bool IsNamed => Name.Length > 0;

    public string CommandId => MacroCommands.RunPrefix + Name;

    /// <summary>For messages: the name, or "the last recorded macro".</summary>
    public string DisplayName => IsNamed ? $"'{Name}'" : "the last recorded macro";

    /// <summary>Whether any step types into the editor (key or text steps); command-only macros also play while Vim emulation is on.</summary>
    public bool TypesIntoEditor => Steps.Any(static s => s.TypesIntoEditor);

    public bool Equals(Macro? other) =>
        other is not null && Name == other.Name && Description == other.Description && Key == other.Key && Steps.SequenceEqual(other.Steps);

    public override int GetHashCode() => HashCode.Combine(Name, Steps.Count);
}

/// <summary>Valid macro names: they become part of a command id (<c>macro.run.&lt;name&gt;</c>).</summary>
public static class MacroNames
{
    public const int MaxLength = 64;

    /// <summary>Letters, digits, '-' and '_', starting with a letter or digit, at most <see cref="MaxLength"/> characters.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength || !char.IsAsciiLetterOrDigit(name[0])) return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') return false;
        return true;
    }

    public const string Rule = "Use letters, digits, '-' and '_' (start with a letter or digit, at most 64 characters).";
}
