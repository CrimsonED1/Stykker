using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.Tests;

// Tray-Symbol des Servers (PLAN-server, offen seit 2026-10-02). Der Test prüft den echten Weg: die Shell
// bekommt das Symbol wirklich zu sehen (Shell_NotifyIcon antwortet), und eine Menünachricht, die an das Fenster
// dieses Symbols geschickt wird, läuft durch die echte Fensterprozedur und kommt als Aktion an.
// Sichtbar ist das Symbol damit für die Kommandozeile – das Bild im Infobereich kann nur der Nutzer prüfen.
[SupportedOSPlatform("windows")]
[TestClass]
public class TrayIconTests
{
    private static readonly TrayIcon.Item[] Menue =
    {
        new(1, Strings.TrayServerWeb),
        new(2, Strings.TrayServerStop),
    };

    // Ohne interaktiven Desktop (Dienst, Sitzung ohne Bildschirm) oder ohne Shell_NotifyIconW gibt es kein Symbol –
    // der Server muss das aushalten, ohne abzubrechen. Genau das prüft der Aufrufer an der Rückgabe.
    [TestMethod]
    public void OhneSymbol_NenntDenGrund_StattAbzubrechen()
    {
        var gezeigt = 0;
        using var tray = new TrayIcon(Menue, _ => gezeigt++, () => { });
        bool ok = tray.TryShow(Strings.TrayServerTip("http://127.0.0.1:8078"), out var grund);
        if (Verfuegbar) Assert.IsTrue(ok, "mit Desktop kommt das Symbol: " + grund);
        else
        {
            Assert.IsFalse(ok, "ohne Symbol kein Erfolg melden");
            Assert.IsFalse(string.IsNullOrEmpty(grund), "mit einem Grund: " + grund);
            Assert.IsFalse(tray.Visible);
        }
        Assert.AreEqual(0, gezeigt, "ohne Klick wird nichts gemeldet");
    }

    // Dasselbe wie oben, nur mit der Voraussetzung, dass es hier überhaupt funktionieren kann – sonst sagt der
    // Test klar, woran es liegt, statt grün zu werden.
    private static bool Verfuegbar => TrayIcon.Possible;

    [TestMethod]
    public void MenueKommtDurchDieEchteFensterprozedurAn()
    {
        if (!Verfuegbar) Assert.Inconclusive("Hier gibt es kein Tray-Symbol (kein Desktop oder user32 ohne Shell_NotifyIconW)");

        var befehle = new List<int>();
        var geklickt = 0;
        using var tray = new TrayIcon(Menue, id => { lock (befehle) befehle.Add(id); }, () => Interlocked.Increment(ref geklickt));
        Assert.IsTrue(tray.TryShow(Strings.TrayServerTip("http://127.0.0.1:8078"), out var grund), "das Symbol kam: " + grund);
        Assert.IsTrue(tray.Visible);
        Assert.AreNotEqual(IntPtr.Zero, tray.Handle, "hinter dem Symbol steht ein Fenster");

        // Linksklick auf dem Symbol: lParam trägt die Mausnachricht (WM_LBUTTONUP)
        Assert.IsTrue(PostMessage(tray.Handle, 0x8000 + 1, IntPtr.Zero, new IntPtr(0x0202)), "Nachricht abgeschickt");
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref geklickt) > 0, TimeSpan.FromSeconds(5)), "der Klick kam an");

        // Ein Menüeintrag: WM_COMMAND mit der Id des Eintrags (das ist der Weg, den das Menü selbst geht)
        Assert.IsTrue(PostMessage(tray.Handle, 0x0111, new IntPtr(2), IntPtr.Zero), "Menübefehl abgeschickt");
        Assert.IsTrue(SpinWait.SpinUntil(() => { lock (befehle) return befehle.Count > 0; }, TimeSpan.FromSeconds(5)), "der Befehl kam an");
        lock (befehle) CollectionAssert.AreEqual(new[] { 2 }, befehle);
    }

    // Zwei Symbole hintereinander (Server neu starten): nichts darf hängen bleiben, kein Fenster darf übrig bleiben.
    [TestMethod]
    public void SymbolLaesstSichWiederAufbauenUndWiederSchliessen()
    {
        if (!Verfuegbar) Assert.Inconclusive("Hier gibt es kein Tray-Symbol (kein Desktop oder user32 ohne Shell_NotifyIconW)");

        for (int i = 0; i < 2; i++)
        {
            var tray = new TrayIcon(Menue, _ => { }, () => { });
            Assert.IsTrue(tray.TryShow("StykkerLLM-Server · http://127.0.0.1:8078", out var grund), $"Durchgang {i}: " + grund);
            tray.Dispose();
            Assert.IsFalse(tray.Visible, $"Durchgang {i}: nach dem Schließen ist das Symbol weg");
            Assert.AreEqual(IntPtr.Zero, tray.Handle, $"Durchgang {i}: das Fenster ist abgemeldet");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
