namespace CsCode.Panels.Debug.Infrastructure;

/// <summary>
/// Implemented by view-models whose row list is occasionally replaced wholesale (a Reset notification makes a list control drop its
/// containers, and with them the keyboard focus). The view asks "does the list have focus?" in <see cref="RowsReplacing"/> (the containers still
/// exist) and puts the focus back on the selected row after <see cref="RowsReplaced"/>.
/// </summary>
public interface IRowsReplacedNotifier
{
    event Action? RowsReplacing;
    event Action? RowsReplaced;
}
