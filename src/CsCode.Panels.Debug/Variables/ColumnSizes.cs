using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CsCode.Panels.Debug.Variables;

/// <summary>Column widths shared by a tree's header and all of its (virtualised) rows, so resizing a header column re-lays-out every row without per-row state.</summary>
public sealed class ColumnSizes : ObservableObject
{
    private GridLength _first = new(220);
    private GridLength _second = new(260);

    public GridLength First { get => _first; set => SetProperty(ref _first, value); }
    public GridLength Second { get => _second; set => SetProperty(ref _second, value); }
}
