using Avalonia.Controls;
using Avalonia.Threading;

namespace StykkerCmd.UI.Panels;

// Ein Panel: Kopf, Filterfeld, Liste und Fußzeile. Zustand und Befehle liegen im PanelViewModel bzw. im Hauptfenster.
public partial class PanelView : UserControl
{
    public PanelView()
    {
        InitializeComponent();
    }

    public bool IsFilterFocused => FilterBox.IsFocused;

    public void SetActive(bool active)
    {
        Frame.Classes.Set("active", active);
    }

    // Fokus auf die Zeile unter dem Cursor. Steht der Fokus nur auf der Liste, verbraucht die erste Pfeiltaste den Wechsel.
    public void FocusList()
    {
        List.Focus();
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not PanelViewModel model || model.CursorIndex < 0)
                return;

            List.ScrollIntoView(model.CursorIndex);
            if (List.ContainerFromIndex(model.CursorIndex) is Control row)
                row.Focus();
        }, DispatcherPriority.Loaded);
    }

    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }
}
