using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Projects;

namespace CsCode.Panels.NewProject.ViewModels;

/// <summary>
/// A generator in the page's list. Until its tool check returns it is <see cref="IsChecking"/>; a generator whose tool is missing stays listed
/// but disabled, with the tool's install hint as its second line.
/// </summary>
public sealed class GeneratorItem(IProjectGenerator generator) : ObservableObject
{
    private bool _isChecking = true;
    private bool _isAvailable;
    private string _detail = generator.Description;
    private string? _toolSummary;
    private string? _installHint;

    public IProjectGenerator Generator { get; } = generator;
    public string Id => Generator.Id;
    public string DisplayName => Generator.DisplayName;
    public string Description => Generator.Description;
    public string IconKey => Generator.IconKey ?? "file-code";

    public bool IsChecking { get => _isChecking; private set { if (SetProperty(ref _isChecking, value)) OnPropertyChanged(nameof(IsSelectable)); } }

    /// <summary>The tool answered: the generator can be chosen.</summary>
    public bool IsAvailable { get => _isAvailable; private set { if (SetProperty(ref _isAvailable, value)) OnPropertyChanged(nameof(IsSelectable)); } }

    /// <summary>The list row is enabled: while the check runs (the remembered generator is selected at once), and when its tool is there.</summary>
    public bool IsSelectable => _isChecking || _isAvailable;

    /// <summary>The second line: the description, or why it is disabled and how to install what is missing.</summary>
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    /// <summary>"cargo 1.96.1 (…)", or what is missing.</summary>
    public string? ToolSummary { get => _toolSummary; private set => SetProperty(ref _toolSummary, value); }

    public string? InstallHint { get => _installHint; private set => SetProperty(ref _installHint, value); }

    internal void Apply(ProjectToolCheck check)
    {
        ToolSummary = check.Summary;
        InstallHint = check.InstallHint;
        IsAvailable = check.IsAvailable;
        Detail = check.IsAvailable ? Description : $"{check.Summary}. {check.InstallHint}".Trim();
        IsChecking = false;
    }
}

/// <summary>One option of the selected generator, as the page edits it (a string value, the option's default to start).</summary>
public abstract class OptionItem(ProjectOption option) : ObservableObject
{
    public ProjectOption Option { get; } = option;
    public string Id => Option.Id;
    public string Label => Option.Label;
    public string? Description => Option.Description;

    /// <summary>The value the generator gets (<see cref="ProjectOption.True"/>/<see cref="ProjectOption.False"/> for a toggle).</summary>
    public abstract string Value { get; set; }
}

public sealed class ChoiceOptionItem : OptionItem
{
    private ProjectOptionChoice? _selected;

    public ChoiceOptionItem(ProjectOption option) : base(option)
    {
        Choices = option.Choices;
        _selected = option.Choices.FirstOrDefault(c => c.Value == option.DefaultValue) ?? option.Choices.FirstOrDefault();
    }

    public IReadOnlyList<ProjectOptionChoice> Choices { get; }

    public ProjectOptionChoice? Selected
    {
        get => _selected;
        set
        {
            if (value is null || !SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(SelectedDescription));
        }
    }

    public string? SelectedDescription => _selected?.Description;

    public override string Value
    {
        get => _selected?.Value ?? "";
        // A remembered value the generator no longer offers (a template uninstalled since) leaves the default.
        set { if (Choices.FirstOrDefault(c => c.Value == value) is { } choice) Selected = choice; }
    }
}

public sealed class ToggleOptionItem(ProjectOption option) : OptionItem(option)
{
    private bool _isChecked = option.DefaultValue == ProjectOption.True;

    public bool IsChecked
    {
        get => _isChecked;
        set { if (SetProperty(ref _isChecked, value)) OnPropertyChanged(nameof(Value)); }
    }

    public override string Value
    {
        get => _isChecked ? ProjectOption.True : ProjectOption.False;
        set { if (value is ProjectOption.True or ProjectOption.False) IsChecked = value == ProjectOption.True; }
    }
}
