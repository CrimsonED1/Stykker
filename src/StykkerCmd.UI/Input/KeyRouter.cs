using Avalonia.Input;

namespace StykkerCmd.UI.Input;

// Befehle der Oberfläche. Die Tastenbelegung steht nur hier.
public enum UiCommand
{
    None,
    SwitchPanel,
    FocusList,
    ToggleTheme,
    Copy,
    Move,
    MakeDirectory,
    Delete,
    DeletePermanent,
    Open,
    GoUp,
    ToggleMark,
    MarkAll,
    SortName,
    SortExtension,
    SortSize,
    SortModified,
    Reload,
    FocusFilter,
    ClearFilter,
    ContextMenu,
    Zip,
    Unzip,
    CopyPath,
    Reveal,
    ShellMenu,
    ShowHelp,
    About,
    Quit,
}

public static class KeyRouter
{
    // textInputFocused: das Filterfeld hat den Fokus. Dann bleiben Buchstaben beim Feld; Funktionstasten gelten weiter.
    public static UiCommand Map(Key key, KeyModifiers modifiers, bool textInputFocused)
    {
        switch (key)
        {
            case Key.Tab when modifiers == KeyModifiers.None:
                return UiCommand.SwitchPanel;
            case Key.F5 when modifiers == KeyModifiers.None:
                return UiCommand.Copy;
            case Key.F6 when modifiers == KeyModifiers.None:
                return UiCommand.Move;
            case Key.F7 when modifiers == KeyModifiers.None:
                return UiCommand.MakeDirectory;
            case Key.F8 when modifiers == KeyModifiers.None:
                return UiCommand.Delete;
            case Key.F8 when modifiers == KeyModifiers.Shift:
                return UiCommand.DeletePermanent;
            case Key.Delete when modifiers == KeyModifiers.Shift:
                return UiCommand.DeletePermanent;
            case Key.F9 when modifiers == KeyModifiers.None:
                return UiCommand.ToggleTheme;
            case Key.F1 when modifiers == KeyModifiers.None:
                return UiCommand.ShowHelp;
            case Key.F10 when modifiers == KeyModifiers.Shift:
                return UiCommand.ContextMenu;
            case Key.Apps when modifiers == KeyModifiers.None:
                return UiCommand.ContextMenu;
        }

        if (textInputFocused)
        {
            return key switch
            {
                Key.Escape => UiCommand.ClearFilter,
                Key.Enter => UiCommand.FocusList,
                _ => UiCommand.None,
            };
        }

        if (modifiers == KeyModifiers.Control)
        {
            return key switch
            {
                Key.A => UiCommand.MarkAll,
                Key.D1 => UiCommand.SortName,
                Key.D2 => UiCommand.SortExtension,
                Key.D3 => UiCommand.SortSize,
                Key.D4 => UiCommand.SortModified,
                Key.R => UiCommand.Reload,
                Key.F => UiCommand.FocusFilter,
                _ => UiCommand.None,
            };
        }

        if (modifiers != KeyModifiers.None)
            return UiCommand.None;

        return key switch
        {
            Key.Enter => UiCommand.Open,
            Key.Back => UiCommand.GoUp,
            Key.Insert => UiCommand.ToggleMark,
            Key.Space => UiCommand.ToggleMark,
            Key.Escape => UiCommand.ClearFilter,
            _ => UiCommand.None,
        };
    }
}
