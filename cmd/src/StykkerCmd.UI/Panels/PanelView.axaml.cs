using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StykkerCmd.UI.Input;

namespace StykkerCmd.UI.Panels;

// Ein Panel: Laufwerkstabs, Kopf, Filterfeld, Liste und Fußzeile. Zustand und Befehle liegen im PanelViewModel bzw. im Hauptfenster.
public partial class PanelView : UserControl
{
    private readonly ContextMenu _entryMenu = new();
    private readonly ContextMenu _panelMenu = new();
    private readonly MenuItem _unzipItem;
    private readonly MenuItem _shellItem;

    public PanelView()
    {
        InitializeComponent();

        _unzipItem = Item("Hier entpacken", UiCommand.Unzip);
        _shellItem = Item("Windows-Menü anzeigen …", UiCommand.ShellMenu);
        BuildEntryMenu();
        BuildPanelMenu();

        // Tunnel: der Rechtsklick kommt vor dem Listeneintrag an, der ihn sonst verbraucht (Auswahl) und das Menü verhindert.
        List.AddHandler(InputElement.PointerPressedEvent,
            new EventHandler<PointerPressedEventArgs>(OnListPointerPressed),
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // Befehl aus dem Kontextmenü. Das Hauptfenster führt ihn aus, nachdem dieses Panel aktiv geworden ist.
    public event Action<UiCommand>? CommandRequested;

    // Der Nutzer hat in dieses Panel geklickt: es soll aktiv werden.
    public event Action? ActivationRequested;

    public bool IsFilterFocused => FilterBox.IsFocused;

    private PanelViewModel? Model => DataContext as PanelViewModel;

    public void SetActive(bool active)
    {
        Frame.Classes.Set("active", active);
    }

    // Das native Menü gibt es nur, wo die Plattform es kann.
    public void ConfigureShell(bool supportsNativeMenu)
    {
        _shellItem.IsVisible = supportsNativeMenu;
    }

    // Fokus auf die Zeile unter dem Cursor. Steht der Fokus nur auf der Liste, verbraucht die erste Pfeiltaste den Wechsel.
    public void FocusList()
    {
        List.Focus();
        Dispatcher.UIThread.Post(() =>
        {
            if (Model is null || Model.CursorIndex < 0)
                return;

            List.ScrollIntoView(Model.CursorIndex);
            if (List.ContainerFromIndex(Model.CursorIndex) is Control row)
                row.Focus();
        }, DispatcherPriority.Loaded);
    }

    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    // Kontextmenü über der Zeile unter dem Cursor (Umschalt+F10 oder Menütaste). Auf ".." und ohne Zeile das Panelmenü.
    public void ShowContextMenu()
    {
        var row = Model is null ? null : List.ContainerFromIndex(Model.CursorIndex);
        if (Model?.CursorRow is { IsParent: false })
            OpenEntryMenu(row ?? List);
        else
            OpenPanelMenu(row ?? List);
    }

    private void OnVolumeClick(object? sender, RoutedEventArgs e)
    {
        ActivationRequested?.Invoke();
        if (sender is Button { Tag: string root } && Model is { } model)
            _ = model.SwitchVolumeAsync(root);
    }

    // Rechtsklick wählt den Eintrag unter dem Zeiger, wie im Explorer, und öffnet sein Menü. Auf leerer Fläche und auf ".."
    // öffnet sich das Panelmenü; der Cursor bleibt dann, wo er war.
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is null || !e.GetCurrentPoint(List).Properties.IsRightButtonPressed)
            return;

        ActivationRequested?.Invoke();
        e.Handled = true;

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not EntryRow { IsParent: false })
        {
            OpenPanelMenu(List);
            return;
        }

        Model.CursorIndex = List.IndexFromContainer(item);
        OpenEntryMenu(item);
    }

    // "Hier entpacken" nur bei einem ZIP-Archiv unter dem Cursor.
    private void OpenEntryMenu(Control target)
    {
        var cursor = Model?.CursorRow;
        _unzipItem.IsVisible = cursor is { IsParent: false, IsDirectory: false }
                               && cursor.Entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        OpenMenuAt(_entryMenu, target);
    }

    private void OpenPanelMenu(Control target) => OpenMenuAt(_panelMenu, target);

    // Das Menü öffnet sich an der Zeigerposition.
    private static void OpenMenuAt(ContextMenu menu, Control target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(target);
    }

    // Schließt beide Kontextmenüs. Gibt zurück, ob eines offen war.
    public bool CloseMenus()
    {
        var wasOpen = _entryMenu.IsOpen || _panelMenu.IsOpen;
        _entryMenu.Close();
        _panelMenu.Close();
        return wasOpen;
    }

    private void BuildEntryMenu()
    {
        _entryMenu.Items.Add(Item("Öffnen", UiCommand.Open));
        _entryMenu.Items.Add(new Separator());
        _entryMenu.Items.Add(Item("Zippen …", UiCommand.Zip));
        _entryMenu.Items.Add(_unzipItem);
        _entryMenu.Items.Add(new Separator());
        _entryMenu.Items.Add(Item("Kopieren … (F5)", UiCommand.Copy));
        _entryMenu.Items.Add(Item("Verschieben … (F6)", UiCommand.Move));
        _entryMenu.Items.Add(Item("Neuer Ordner … (F7)", UiCommand.MakeDirectory));
        _entryMenu.Items.Add(new Separator());
        _entryMenu.Items.Add(Item("Markieren oder lösen (Leertaste)", UiCommand.ToggleMark));
        _entryMenu.Items.Add(Item("Alle markieren oder lösen (Strg+A)", UiCommand.MarkAll));
        _entryMenu.Items.Add(new Separator());
        _entryMenu.Items.Add(Item("In den Papierkorb (F8)", UiCommand.Delete));
        _entryMenu.Items.Add(Item("Endgültig löschen … (Umschalt+F8)", UiCommand.DeletePermanent));
        _entryMenu.Items.Add(new Separator());
        _entryMenu.Items.Add(Item("Pfad kopieren", UiCommand.CopyPath));
        _entryMenu.Items.Add(Item("Im Ordner anzeigen", UiCommand.Reveal));
        _entryMenu.Items.Add(new Separator());
        _entryMenu.Items.Add(_shellItem);
    }

    // Menü auf leerer Fläche: Befehle, die das ganze Panel betreffen, nicht einen Eintrag.
    private void BuildPanelMenu()
    {
        _panelMenu.Items.Add(Item("Neuer Ordner … (F7)", UiCommand.MakeDirectory));
        _panelMenu.Items.Add(Item("Ordner neu laden (Strg+R)", UiCommand.Reload));
        _panelMenu.Items.Add(new Separator());
        _panelMenu.Items.Add(SortMenu());
        _panelMenu.Items.Add(new Separator());
        _panelMenu.Items.Add(Item("Alle markieren oder lösen (Strg+A)", UiCommand.MarkAll));
    }

    private MenuItem SortMenu()
    {
        var sort = new MenuItem { Header = "Sortieren nach" };
        sort.Items.Add(Item("Name (Strg+1)", UiCommand.SortName));
        sort.Items.Add(Item("Erweiterung (Strg+2)", UiCommand.SortExtension));
        sort.Items.Add(Item("Größe (Strg+3)", UiCommand.SortSize));
        sort.Items.Add(Item("Datum (Strg+4)", UiCommand.SortModified));
        return sort;
    }

    private MenuItem Item(string header, UiCommand command)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => CommandRequested?.Invoke(command);
        return item;
    }
}
