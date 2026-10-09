using Avalonia.Controls;

namespace StykkerCmd.UI.Dialogs;

// Eingabe eines Namens. Liefert den Text oder null bei Abbruch.
public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public static Task<string?> ShowAsync(Window owner, string title, string prompt, string initial, string okText = "OK")
    {
        var dialog = new InputDialog { Title = title };
        dialog.Prompt.Text = prompt;
        dialog.Input.Text = initial;
        dialog.OkButton.Content = okText;
        dialog.OkButton.Click += (_, _) => dialog.Close(dialog.Input.Text);
        dialog.CancelButton.Click += (_, _) => dialog.Close(null);
        return dialog.ShowDialog<string?>(owner);
    }
}
