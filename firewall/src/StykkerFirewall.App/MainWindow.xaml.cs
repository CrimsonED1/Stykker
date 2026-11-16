using System.ComponentModel;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using StykkerFirewall.App.Components;

namespace StykkerFirewall.App;

// Nur der Rahmen: die Übersicht ist eine Razor-Seite. Schließen versteckt das Fenster, die App bleibt im Tray.
public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        ThemeSettings.FollowBackground(this, services);
        WebView.Services = services;
        WebView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Overview) });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
