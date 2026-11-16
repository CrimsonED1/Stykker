using System.ComponentModel;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using StykkerFirewall.App.Components;

namespace StykkerFirewall.App;

// Kleines Fenster unten rechts für die Nachfrage. Bleibt nach dem ersten Mal bestehen (nur versteckt), damit die
// nächste Nachfrage ohne Startzeit von WebView2 erscheint. Schließen beantwortet nichts: die Nachfrage bleibt offen
// (Übersicht, Reiter „Waiting“) und das Fenster kommt beim nächsten Ereignis wieder.
public partial class PromptWindow : Window
{
    public PromptWindow(IServiceProvider services)
    {
        InitializeComponent();
        WebView.Services = services;
        WebView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(PromptView) });
    }

    public void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
