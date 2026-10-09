using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StykkerCmd.Core;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Elevation;
using StykkerCmd.Core.Listing;
using StykkerCmd.Core.Model;
using StykkerCmd.Core.Operations;
using StykkerCmd.UI.Dialogs;
using StykkerCmd.UI.Input;
using StykkerCmd.UI.Operations;
using StykkerCmd.UI.Panels;
using StykkerCmd.UI.Text;
using StykkerCmd.UI.Theming;

namespace StykkerCmd.UI;

// Hauptfenster: zwei Panels, Funktionsleiste und die Befehle für Tasten und Schaltflächen.
public partial class MainWindow : Window
{
    private const string Hint = "Tab Panel · Enter öffnen · Leertaste markieren · Umschalt+F10 Kontextmenü · "
                                + "Strg+A alle · Strg+1–4 sortieren · Strg+F filtern · F1 Hilfe";

    private static readonly string HelpText = string.Join('\n',
        "Tab  Panel wechseln",
        "Pfeiltasten, Pos1, Ende, Bild auf/ab  Cursor bewegen",
        "Enter  Ordner öffnen oder Datei öffnen",
        "Rücktaste  einen Ordner nach oben",
        "Leertaste oder Einfg  markieren, eine Zeile weiter",
        "Strg+A  alle markieren, zweimal löst die Markierung",
        "F5 Kopieren · F6 Verschieben · F7 Ordner · F8 Papierkorb",
        "Umschalt+F8  endgültig löschen (doppelt bestätigt)",
        "Strg+1 bis Strg+4  sortieren, zweimal kehrt die Richtung um",
        "Strg+F  Filter · Esc  Filter leeren oder Auftrag abbrechen",
        "Strg+R  neu laden · F9  Thema wechseln",
        "Umschalt+F10 oder Kontextmenü-Taste  Kontextmenü");

    private readonly IPlatformServices _services;
    private readonly ThemeService _themes;
    private readonly PanelViewModel _leftModel;
    private readonly PanelViewModel _rightModel;
    private readonly OperationController _operations;
    private bool _leftActive = true;
    private Action? _cancelOperation;

    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current!;
        _services = app.Services;
        _themes = app.Themes;
        _operations = new OperationController(_services, this);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var left = Directory.Exists(StartupOptions.LeftDirectory) ? StartupOptions.LeftDirectory! : home;
        var right = Directory.Exists(StartupOptions.RightDirectory) ? StartupOptions.RightDirectory! : home;
        _leftModel = new PanelViewModel(_services.FileSystem, left);
        _rightModel = new PanelViewModel(_services.FileSystem, right);
        LeftPanel.DataContext = _leftModel;
        RightPanel.DataContext = _rightModel;

        Title = AppInfo.Name;
        StatusText.Text = Hint;
        UpdateThemeLabel();

        // Die Panels melden Klicks und Befehle aus ihrem Kontextmenü an das Hauptfenster.
        LeftPanel.CommandRequested += command => RunFromPanel(left: true, command);
        RightPanel.CommandRequested += command => RunFromPanel(left: false, command);
        LeftPanel.ActivationRequested += () => ActivatePanel(left: true);
        RightPanel.ActivationRequested += () => ActivatePanel(left: false);
        LeftPanel.ConfigureShell(_services.Shell.SupportsNativeMenu);
        RightPanel.ConfigureShell(_services.Shell.SupportsNativeMenu);
        ShellMenuItem.IsVisible = _services.Shell.SupportsNativeMenu;

        // Tunnel: Tasten kommen zuerst am Fenster an, bevor Listen oder Textfelder sie verbrauchen.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Opened += OnOpened;
    }

    private PanelViewModel Active => _leftActive ? _leftModel : _rightModel;

    private PanelViewModel Other => _leftActive ? _rightModel : _leftModel;

    private PanelView ActiveView => _leftActive ? LeftPanel : RightPanel;

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await _leftModel.LoadAsync();
            await _rightModel.LoadAsync();
            // Erst nach dem Layout fokussieren, sonst übernimmt das Fenster den Fokus wieder.
            Dispatcher.UIThread.Post(() => ActivatePanel(left: true), DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Fehler", ex.Message);
        }
    }

    // ---- Fortschritt ------------------------------------------------------------------------

    public void ShowProgress(string heading, Action cancel)
    {
        _cancelOperation = cancel;
        ProgressHeading.Text = heading;
        ProgressCurrent.Text = string.Empty;
        ProgressCounts.Text = string.Empty;
        ProgressMeter.IsIndeterminate = true;
        ProgressCancel.IsEnabled = true;
        ProgressLayer.IsVisible = true;
    }

    public void UpdateProgress(OperationProgress progress)
    {
        ProgressHeading.Text = progress.Phase;
        ProgressCurrent.Text = progress.CurrentPath ?? string.Empty;
        ProgressCounts.Text = $"{progress.FilesDone} von {progress.FilesTotal} Dateien · "
                              + $"{Format.Size(progress.BytesDone)} von {Format.Size(progress.BytesTotal)}";

        if (progress.BytesTotal > 0)
        {
            ProgressMeter.IsIndeterminate = false;
            ProgressMeter.Value = Math.Min(100, 100.0 * progress.BytesDone / progress.BytesTotal);
        }
        else if (progress.FilesTotal > 0)
        {
            ProgressMeter.IsIndeterminate = false;
            ProgressMeter.Value = Math.Min(100, 100.0 * progress.FilesDone / progress.FilesTotal);
        }
        else
        {
            ProgressMeter.IsIndeterminate = true;
        }
    }

    public void HideProgress()
    {
        ProgressLayer.IsVisible = false;
        _cancelOperation = null;
    }

    private void OnProgressCancel(object? sender, RoutedEventArgs e)
    {
        if (!ProgressCancel.IsEnabled)
            return;
        ProgressCancel.IsEnabled = false;
        ProgressHeading.Text = "Wird abgebrochen …";
        _cancelOperation?.Invoke();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Während eines Auftrags wirken keine Befehle; Esc bricht ab.
        if (ProgressLayer.IsVisible)
        {
            if (e.Key == Key.Escape)
                OnProgressCancel(null, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Menüs und das Kontextmenü bedienen sich selbst: Pfeiltasten, Enter und Esc gehören ihnen, nicht den Listen.
        if (e.Source is Visual source
            && (source.FindAncestorOfType<MenuItem>(includeSelf: true) is not null
                || source.FindAncestorOfType<ContextMenu>(includeSelf: true) is not null))
            return;

        var command = KeyRouter.Map(e.Key, e.KeyModifiers, LeftPanel.IsFilterFocused || RightPanel.IsFilterFocused);
        if (command == UiCommand.None)
            return;

        e.Handled = true;
        Dispatch(command);
    }

    private void OnFunctionButton(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<UiCommand>(name, out var command))
            Dispatch(command);
    }

    // Menüeintrag: sein Tag trägt den Befehlsnamen, wie bei den Funktionsknöpfen.
    private void OnMenuCommand(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } && Enum.TryParse<UiCommand>(name, out var command))
            Dispatch(command);
    }

    private async void Dispatch(UiCommand command)
    {
        try
        {
            await ExecuteAsync(command);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Fehler", ex.Message);
        }
        finally
        {
            if (command is UiCommand.Copy or UiCommand.Move or UiCommand.MakeDirectory
                or UiCommand.Delete or UiCommand.DeletePermanent or UiCommand.Open
                or UiCommand.Zip or UiCommand.Unzip)
            {
                RestoreFocus();
            }
        }
    }

    // Nach einem Dialog geht der Vordergrund oft an ein anderes Programm. Das Hauptfenster holt ihn zurück
    // und setzt den Fokus wieder auf die aktive Liste, damit die Tasten dort ankommen.
    public void RestoreFocus()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _services.WindowFocus.BringToFront(handle);
        ActiveView.FocusList();
    }

    private async Task ExecuteAsync(UiCommand command)
    {
        switch (command)
        {
            case UiCommand.SwitchPanel:
                ActivatePanel(!_leftActive);
                break;
            case UiCommand.FocusList:
                ActiveView.FocusList();
                break;
            case UiCommand.ToggleTheme:
                _themes.Next();
                UpdateThemeLabel();
                break;
            case UiCommand.Copy:
                await TransferAsync(move: false);
                break;
            case UiCommand.Move:
                await TransferAsync(move: true);
                break;
            case UiCommand.MakeDirectory:
                await MakeDirectoryAsync();
                break;
            case UiCommand.Delete:
                await DeleteAsync(permanent: false);
                break;
            case UiCommand.DeletePermanent:
                await DeleteAsync(permanent: true);
                break;
            case UiCommand.Open:
                await OpenCursorAsync();
                break;
            case UiCommand.GoUp:
                await Active.GoUpAsync();
                break;
            case UiCommand.ToggleMark:
                Active.ToggleMarkAtCursor();
                break;
            case UiCommand.MarkAll:
                Active.MarkAll();
                break;
            case UiCommand.SortName:
                Active.Sort(SortField.Name);
                break;
            case UiCommand.SortExtension:
                Active.Sort(SortField.Extension);
                break;
            case UiCommand.SortSize:
                Active.Sort(SortField.Size);
                break;
            case UiCommand.SortModified:
                Active.Sort(SortField.Modified);
                break;
            case UiCommand.Reload:
                await Active.LoadAsync();
                break;
            case UiCommand.FocusFilter:
                ActiveView.FocusFilter();
                break;
            case UiCommand.ClearFilter:
                Active.ClearFilter();
                ActiveView.FocusList();
                break;
            case UiCommand.Zip:
                await ZipAsync();
                break;
            case UiCommand.Unzip:
                await UnzipAsync();
                break;
            case UiCommand.CopyPath:
                await CopyPathAsync();
                break;
            case UiCommand.Reveal:
                await RevealAsync();
                break;
            case UiCommand.ShellMenu:
                // Das native Menü hält den UI-Thread an. Erst nach dem Schließen des eigenen Menüs öffnen.
                Dispatcher.UIThread.Post(ShowShellMenu, DispatcherPriority.Background);
                break;
            case UiCommand.ContextMenu:
                ActiveView.ShowContextMenu();
                break;
            case UiCommand.ShowHelp:
                await MessageDialog.ShowAsync(this, "Tastenübersicht", "Tasten und Befehle", HelpText,
                    new DialogButton("OK", "ok", IsDefault: true, IsCancel: true));
                break;
            case UiCommand.About:
                await MessageDialog.ShowAsync(this, "Über StykkerCMD", AppInfo.Name,
                    "Zwei-Panel-Dateimanager für Windows und Linux. Ohne Browser, Server oder offenen Port.",
                    new DialogButton("OK", "ok", IsDefault: true, IsCancel: true));
                break;
            case UiCommand.Quit:
                Close();
                break;
        }
    }

    // Befehl aus dem Kontextmenü eines Panels: dieses Panel wird zuerst aktiv, danach läuft der Befehl.
    private void RunFromPanel(bool left, UiCommand command)
    {
        ActivatePanel(left);
        Dispatch(command);
    }

    private void ActivatePanel(bool left)
    {
        _leftActive = left;
        LeftPanel.SetActive(left);
        RightPanel.SetActive(!left);
        ActiveView.FocusList();
    }

    private void UpdateThemeLabel()
    {
        ThemeLabel.Text = $"Thema: {_themes.Current.Name}";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ---- Kopieren und Verschieben -------------------------------------------------------------

    private async Task TransferAsync(bool move)
    {
        var sources = Active.SelectedEntries();
        if (sources.Count == 0)
            return;

        var request = new OperationRequest(
            move ? OperationKind.Move : OperationKind.Copy,
            sources.Select(s => s.FullPath).ToList(),
            Other.CurrentDirectory);

        var result = await _operations.RunAsync(request, move ? "Verschieben" : "Kopieren");
        await FinishAsync(result, move ? "Verschoben" : "Kopiert");
    }

    // ---- Ordner anlegen ----------------------------------------------------------------------

    private async Task MakeDirectoryAsync()
    {
        var name = (await InputDialog.ShowAsync(this, "Neuer Ordner", "Name des neuen Ordners:", "Neuer Ordner", "Anlegen"))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await ShowMessageAsync("Ungültiger Name", "Der Name enthält Zeichen, die das Dateisystem nicht erlaubt.");
            return;
        }

        var path = _services.FileSystem.Combine(Active.CurrentDirectory, name);
        var result = await _operations.RunAsync(new OperationRequest(OperationKind.CreateDirectory, [path]), "Ordner anlegen");

        await Active.LoadAsync(selectName: name);
        if (result.Issues.Count > 0)
            await ShowIssuesAsync(result);
        else
            SetStatus($"Ordner „{name}“ angelegt.");
    }

    // ---- Löschen -----------------------------------------------------------------------------

    // F8: in den Papierkorb, nach Bestätigung. Auf Datenträgern ohne Papierkorb nur endgültig, nach doppelter Bestätigung.
    // Umschalt+F8: immer endgültig, ebenfalls doppelt bestätigt.
    private async Task DeleteAsync(bool permanent)
    {
        var sources = Active.SelectedEntries();
        if (sources.Count == 0)
            return;

        var paths = sources.Select(s => s.FullPath).ToList();
        var summary = sources.Count == 1 ? $"„{sources[0].Name}“" : $"{sources.Count} Elemente";

        if (permanent)
        {
            if (await ConfirmPermanentAsync(summary))
                await RunDeleteAsync(OperationKind.DeletePermanent, paths);
            return;
        }

        var confirmed = await MessageDialog.ShowAsync(
            this,
            "Löschen",
            $"{summary} in den Papierkorb verschieben?",
            "Im Papierkorb lassen sie sich wiederherstellen.",
            new DialogButton("In den Papierkorb", "yes", IsDefault: true),
            new DialogButton("Abbrechen", "no", IsCancel: true));
        if (confirmed != "yes")
            return;

        var trashable = paths.Where(p => _services.Trash.IsAvailable(p)).ToList();
        var withoutTrash = paths.Except(trashable).ToList();

        if (trashable.Count > 0)
            await RunDeleteAsync(OperationKind.DeleteToTrash, trashable);

        if (withoutTrash.Count > 0)
        {
            var choice = await MessageDialog.ShowAsync(
                this,
                "Kein Papierkorb",
                $"{withoutTrash.Count} Elemente liegen auf einem Datenträger ohne Papierkorb.",
                "Dort lassen sie sich nicht wiederherstellen. Endgültig löschen?",
                new DialogButton("Endgültig löschen …", "yes"),
                new DialogButton("Abbrechen", "no", IsDefault: true, IsCancel: true));
            if (choice == "yes" && await ConfirmPermanentAsync($"{withoutTrash.Count} Elemente"))
                await RunDeleteAsync(OperationKind.DeletePermanent, withoutTrash);
        }
    }

    private async Task RunDeleteAsync(OperationKind kind, IReadOnlyList<string> paths)
    {
        var heading = kind == OperationKind.DeleteToTrash ? "In den Papierkorb" : "Endgültig löschen";
        var result = await _operations.RunAsync(new OperationRequest(kind, paths), heading);
        await FinishAsync(result, kind == OperationKind.DeleteToTrash ? "Im Papierkorb" : "Gelöscht");
    }

    private async Task<bool> ConfirmPermanentAsync(string summary)
    {
        var first = await MessageDialog.ShowAsync(
            this,
            "Endgültig löschen",
            $"{summary} endgültig löschen?",
            "Das lässt sich nicht rückgängig machen.",
            new DialogButton("Weiter", "yes"),
            new DialogButton("Abbrechen", "no", IsDefault: true, IsCancel: true));
        if (first != "yes")
            return false;

        var second = await MessageDialog.ShowAsync(
            this,
            "Wirklich endgültig löschen?",
            "Letzte Bestätigung: danach ist der Inhalt weg.",
            string.Empty,
            new DialogButton("Endgültig löschen", "yes"),
            new DialogButton("Abbrechen", "no", IsDefault: true, IsCancel: true));
        return second == "yes";
    }

    // ---- Öffnen ------------------------------------------------------------------------------

    private async Task OpenCursorAsync()
    {
        var file = await Active.ActivateCursorAsync();
        if (file is null)
            return;

        try
        {
            _services.Shell.Open(file.FullPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShowMessageAsync("Öffnen fehlgeschlagen", ex.Message);
        }
    }

    // ---- Zippen und Entpacken ----------------------------------------------------------------

    private async Task ZipAsync()
    {
        var sources = Active.SelectedEntries();
        if (sources.Count == 0)
            return;

        // Vorschlag: der Name des einzigen Eintrags (Datei ohne Endung), sonst "Archiv".
        var suggestion = sources.Count != 1
            ? "Archiv"
            : sources[0].IsDirectory ? sources[0].Name : Path.GetFileNameWithoutExtension(sources[0].Name);
        var typed = (await InputDialog.ShowAsync(this, "Zippen", "Name des Archivs:", suggestion + ".zip", "Zippen"))?.Trim();
        if (string.IsNullOrEmpty(typed))
            return;

        var archive = typed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? typed : typed + ".zip";
        if (archive.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await ShowMessageAsync("Ungültiger Name", "Der Name enthält Zeichen, die das Dateisystem nicht erlaubt.");
            return;
        }

        var result = await _operations.RunAsync(
            new OperationRequest(OperationKind.Zip, sources.Select(s => s.FullPath).ToList(), Active.CurrentDirectory, archive),
            "Zippen");
        await FinishAsync(result, "Gezippt");
        await Active.LoadAsync(selectName: archive);
    }

    // Entpackt jedes markierte ZIP-Archiv in einen Ordner gleichen Namens im aktuellen Ordner.
    private async Task UnzipAsync()
    {
        var archives = Active.SelectedEntries()
            .Where(e => !e.IsDirectory && e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (archives.Count == 0)
        {
            SetStatus("Kein ZIP-Archiv ausgewählt.");
            return;
        }

        foreach (var archive in archives)
        {
            var result = await _operations.RunAsync(
                new OperationRequest(OperationKind.Unzip, [archive.FullPath], Active.CurrentDirectory),
                "Entpacken");
            await FinishAsync(result, "Entpackt");
        }
    }

    // ---- Pfad, Explorer und natives Menü ------------------------------------------------------

    private async Task CopyPathAsync()
    {
        var paths = Active.SelectedEntries().Select(e => e.FullPath).ToList();
        if (paths.Count == 0)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            SetStatus("Die Zwischenablage ist nicht verfügbar.");
            return;
        }

        await clipboard.SetTextAsync(string.Join(Environment.NewLine, paths));
        SetStatus(paths.Count == 1 ? "Pfad in der Zwischenablage." : $"{paths.Count} Pfade in der Zwischenablage.");
    }

    private async Task RevealAsync()
    {
        var entry = Active.SelectedEntries().FirstOrDefault();
        if (entry is null)
            return;

        try
        {
            _services.Shell.Reveal(entry.FullPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ShowMessageAsync("Anzeigen fehlgeschlagen", ex.Message);
        }
    }

    // Das native Kontextmenü der Shell für die Auswahl, wie im Explorer. Danach holt das Fenster den Fokus zurück.
    private async void ShowShellMenu()
    {
        try
        {
            var paths = Active.SelectedEntries().Select(e => e.FullPath).ToList();
            if (paths.Count == 0 || !_services.Shell.SupportsNativeMenu)
                return;

            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            _services.Shell.ShowNativeMenu(paths, handle);
            RestoreFocus();
        }
        catch (Exception ex)
        {
            // Läuft außerhalb von Dispatch, daher hier melden.
            await ShowMessageAsync("Windows-Menü", ex.Message);
        }
    }

    // ---- Ergebnisse und Rechte ---------------------------------------------------------------

    // Nach jedem Auftrag: beide Panels neu laden, Ergebnis melden, bei verweigertem Zugriff die Rechteanhebung anbieten.
    private async Task FinishAsync(OperationResult result, string verb)
    {
        await _leftModel.LoadAsync();
        await _rightModel.LoadAsync();
        SetStatus(Summarize(verb, result));

        if (result.Issues.Any(i => i.Reason == IssueReason.AccessDenied))
            await OfferElevatedRetryAsync(result);
        else if (result.Issues.Count > 0)
            await ShowIssuesAsync(result);
    }

    private async Task OfferElevatedRetryAsync(OperationResult result)
    {
        var leaves = result.Issues
            .Where(i => i.Reason == IssueReason.AccessDenied)
            .Select(i => i.Leaf)
            .ToList();

        var answer = await MessageDialog.ShowAsync(
            this,
            "Zugriff verweigert",
            $"{leaves.Count} Elemente konnten nicht verarbeitet werden.",
            "Mit Administratorrechten wiederholen? Die Erhöhung gilt nur für diese Elemente.",
            new DialogButton("Mit Rechten wiederholen", "retry"),
            new DialogButton("Abbrechen", "cancel", IsDefault: true, IsCancel: true));
        if (answer != "retry")
            return;

        bool anyFailed = false;
        foreach (var batch in leaves.Chunk(ElevatedHelper.MaxOperations))
        {
            var elevation = await _services.Elevation.RunHelperAsync(ElevatedHelper.Encode(batch), CancellationToken.None);
            if (elevation.Outcome == ElevationOutcome.Denied)
            {
                SetStatus("Die Rechteanhebung wurde abgelehnt.");
                return;
            }
            if (elevation.Outcome == ElevationOutcome.Unsupported)
            {
                await ShowMessageAsync("Nicht verfügbar", "Die Rechteanhebung ist auf diesem System nicht verfügbar.");
                return;
            }
            if (elevation.ExitCode != ElevatedHelper.ExitOk)
                anyFailed = true;
        }

        await _leftModel.LoadAsync();
        await _rightModel.LoadAsync();
        SetStatus(anyFailed
            ? "Mit Administratorrechten: einige Schritte sind fehlgeschlagen."
            : "Mit Administratorrechten erledigt.");
    }

    private async Task ShowIssuesAsync(OperationResult result)
    {
        var lines = new List<string>();
        foreach (var issue in result.Issues.Take(8))
        {
            var line = $"{issue.Leaf.Source}: {issue.Message}";
            if (issue.Reason == IssueReason.Locked)
            {
                var holders = _services.LockInspector.FindHolders(issue.Leaf.Source);
                if (holders.Count > 0)
                    line += $" Verwendet von {string.Join(", ", holders.Select(h => $"{h.ProcessName} (PID {h.ProcessId})"))}.";
            }
            lines.Add(line);
        }
        if (result.Issues.Count > 8)
            lines.Add($"… und {result.Issues.Count - 8} weitere.");

        await MessageDialog.ShowAsync(
            this,
            "Nicht alles hat geklappt",
            $"{result.Issues.Count} Meldungen",
            string.Join('\n', lines),
            new DialogButton("OK", "ok", IsDefault: true, IsCancel: true));
    }

    private static string Summarize(string verb, OperationResult result)
    {
        var text = $"{verb}: {result.FilesDone} Dateien ({Format.Size(result.BytesDone)})";
        if (result.Skipped > 0)
            text += $" · {result.Skipped} übersprungen";
        if (result.Issues.Count > 0)
            text += $" · {result.Issues.Count} Meldungen";
        if (result.PartialFiles.Count > 0)
            text += $" · {result.PartialFiles.Count} Teilstände markiert";
        if (result.Cancelled)
            text += " · abgebrochen";
        return text;
    }

    private Task ShowMessageAsync(string title, string body)
        => MessageDialog.ShowAsync(this, title, title, body, new DialogButton("OK", "ok", IsDefault: true, IsCancel: true));
}
