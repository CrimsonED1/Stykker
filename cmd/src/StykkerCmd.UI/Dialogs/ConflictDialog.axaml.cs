using Avalonia.Controls;
using Avalonia.Interactivity;
using StykkerCmd.Core.Operations;
using StykkerCmd.UI.Text;

namespace StykkerCmd.UI.Dialogs;

// Fragt bei einem Namenskonflikt, was geschehen soll. Schließen über das Fenster gilt als Abbruch.
public partial class ConflictDialog : Window
{
    public ConflictDialog()
    {
        InitializeComponent();
        // Vorgabe ist "Überspringen": Enter lässt die Datei unangetastet.
        Loaded += (_, _) => SkipButton.Focus();
    }

    public static Task<ConflictAnswer?> ShowAsync(Window owner, ConflictRequest request)
    {
        var dialog = new ConflictDialog();
        var kind = request.SourceIsDirectory || request.TargetIsDirectory ? "Ordner" : "Datei";
        dialog.Heading.Text = $"{kind} „{Path.GetFileName(request.Target)}“ existiert bereits";
        dialog.TargetInfo.Text = Describe(request.TargetIsDirectory, request.TargetSize, request.TargetModified);
        dialog.SourceInfo.Text = Describe(request.SourceIsDirectory, request.SourceSize, request.SourceModified);
        dialog.OverwriteButton.IsEnabled = request.CanOverwrite;
        return dialog.ShowDialog<ConflictAnswer?>(owner);
    }

    private void OnChoice(object? sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag as string;
        var choice = tag switch
        {
            "overwrite" => ConflictChoice.Overwrite,
            "rename" => ConflictChoice.Rename,
            "skip" => ConflictChoice.Skip,
            _ => ConflictChoice.Cancel,
        };
        Close(new ConflictAnswer(choice, ApplyToAll.IsChecked == true));
    }

    private static string Describe(bool isDirectory, long size, DateTimeOffset modified)
        => $"{(isDirectory ? "Ordner" : Format.Size(size))} · {Format.Date(modified)}";
}
