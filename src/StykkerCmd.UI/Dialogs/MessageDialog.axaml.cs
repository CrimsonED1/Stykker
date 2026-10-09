using Avalonia.Controls;

namespace StykkerCmd.UI.Dialogs;

// Ein Knopf im Hinweisdialog. Result ist der Text, den ShowAsync zurückgibt.
public sealed record DialogButton(string Text, string Result, bool IsDefault = false, bool IsCancel = false);

// Hinweis mit frei wählbaren Knöpfen. Liefert das Result des gedrückten Knopfs, bei Schließen über das Fenster null.
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public static Task<string?> ShowAsync(Window owner, string title, string heading, string body, params DialogButton[] buttons)
    {
        var dialog = new MessageDialog { Title = title };
        dialog.Heading.Text = heading;
        dialog.Body.Text = body;
        dialog.Body.IsVisible = body.Length > 0;

        foreach (var spec in buttons)
        {
            var button = new Button
            {
                Content = spec.Text,
                IsDefault = spec.IsDefault,
                IsCancel = spec.IsCancel,
            };
            if (spec.IsDefault)
                button.Classes.Add("primary");

            var result = spec.Result;
            button.Click += (_, _) => dialog.Close(result);
            dialog.Buttons.Children.Add(button);
        }

        // Der Knopf mit IsDefault ist die sichere Vorgabe (bei endgültigem Löschen "Abbrechen"). Er bekommt den Fokus,
        // damit Enter nicht den ersten Knopf der Liste auslöst.
        var safeDefault = dialog.Buttons.Children.OfType<Button>().FirstOrDefault(b => b.IsDefault);
        if (safeDefault is not null)
            dialog.Loaded += (_, _) => safeDefault.Focus();

        return dialog.ShowDialog<string?>(owner);
    }
}
