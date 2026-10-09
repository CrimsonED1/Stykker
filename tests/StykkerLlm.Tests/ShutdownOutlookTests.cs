using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Rückfrage beim Schließen des Fensters: was das Beenden mit dem Server macht (ServerHolds.Outlook), ohne Netz
[TestClass]
public class ShutdownOutlookTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0);

    [TestMethod]
    public void Outlook_OnlyThisWindow_ServerWouldStop()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:7", T0);
        h.CircuitUp();                                   // die Seite des Fensters selbst
        var o = h.Outlook(T0, "ui:7", ownPages: 1, keepRunning: false, busy: false, proxyActive: 0);
        Assert.IsTrue(o.WouldStop);
        Assert.AreEqual(0, o.OtherPages, "die eigene Seite zählt nicht");
        StringAssert.Contains(o.Hint().Note, "shuts the server down");
        Assert.IsNull(o.Hint().Warning);
    }

    [TestMethod]
    public void Outlook_AnotherWindowOpen_ServerStaysOn()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:7", T0);
        h.Touch("ui:8", T0);
        var o = h.Outlook(T0, "ui:7", ownPages: 0, keepRunning: false, busy: false, proxyActive: 0);
        Assert.AreEqual(1, o.OtherWindows);
        Assert.IsFalse(o.WouldStop);
        StringAssert.Contains(o.Hint().Note, "1 window");
    }

    [TestMethod]
    public void Outlook_TerminalAndWebPage_CountedSeparately()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:7", T0);
        h.Touch("tui:9", T0);
        h.CircuitUp();                                   // die eigene Seite
        h.CircuitUp();                                   // eine Webseite im Browser
        var o = h.Outlook(T0, "ui:7", ownPages: 1, keepRunning: false, busy: false, proxyActive: 0);
        Assert.AreEqual(1, o.OtherTerminals);
        Assert.AreEqual(1, o.OtherPages);
        Assert.IsFalse(o.WouldStop);
        StringAssert.Contains(o.Hint().Note, "1 terminal");
        StringAssert.Contains(o.Hint().Note, "1 web page");
    }

    [TestMethod]
    public void Outlook_TerminalThatEnded_IsNotCounted()
    {
        var h = new ServerHolds(T0, pid => pid == 7);    // tui:8 läuft nicht mehr (Konsole geschlossen)
        h.Touch("ui:7", T0);
        h.Touch("tui:8", T0);
        var o = h.Outlook(T0, "ui:7", ownPages: 0, keepRunning: false, busy: false, proxyActive: 0);
        Assert.AreEqual(0, o.OtherTerminals);
        Assert.IsTrue(o.WouldStop);
    }

    [TestMethod]
    public void Outlook_ExpiredLease_IsNotCounted()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:7", T0);
        h.Touch("tui:9", T0);
        var o = h.Outlook(T0 + ServerHolds.Lease, "ui:7", ownPages: 0, keepRunning: false, busy: false, proxyActive: 0);
        Assert.AreEqual(0, o.OtherTerminals, "die Miete ist abgelaufen");
    }

    [TestMethod]
    public void Outlook_BusyOrKeptServer_StaysOn()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:7", T0);
        var busy = h.Outlook(T0, "ui:7", 0, keepRunning: false, busy: true, proxyActive: 0);
        Assert.IsFalse(busy.WouldStop);
        StringAssert.Contains(busy.Hint().Note, "benchmark");
        var kept = h.Outlook(T0, "ui:7", 0, keepRunning: true, busy: false, proxyActive: 0);
        Assert.IsFalse(kept.WouldStop);
        StringAssert.Contains(kept.Hint().Note, "Keep the server running");
    }

    [TestMethod]
    public void Outlook_ProxyRequestRunning_WarnsWhenTheServerWouldStop()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:7", T0);
        var o = h.Outlook(T0, "ui:7", 0, keepRunning: false, busy: false, proxyActive: 2);
        Assert.IsTrue(o.WouldStop);
        Assert.AreEqual(2, o.ProxyActive);
        Assert.AreEqual(2, o.Hint().ProxyActive);
        Assert.IsNotNull(o.Hint().Warning);
        StringAssert.Contains(o.Hint().Warning!, "cut off");
    }

    [TestMethod]
    public void Outlook_ProxyRequestRunning_ServerStaysOn_OnlyNoted()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:7", T0);
        h.Touch("ui:8", T0);
        var o = h.Outlook(T0, "ui:7", 0, keepRunning: false, busy: false, proxyActive: 1);
        Assert.IsFalse(o.WouldStop);
        Assert.IsNull(o.Hint().Warning, "der Server bleibt an, nichts bricht ab");
        StringAssert.Contains(o.Hint().Note, "request through the proxy");
        Assert.AreEqual(1, o.Hint().ProxyActive);
    }

    [TestMethod]
    public void Outlook_WouldStop_MatchesWhatHappensAfterTheWindowLeaves()
    {
        var h = new ServerHolds(T0);
        h.Touch("ui:7", T0);
        h.CircuitUp();
        Assert.IsTrue(h.Outlook(T0, "ui:7", ownPages: 1, keepRunning: false, busy: false, proxyActive: 0).WouldStop);
        h.Release("ui:7");                               // das Fenster meldet sich ab
        h.CircuitDown();                                 // und seine Seite ist weg
        Assert.IsFalse(h.ShouldStop(T0, false), "die kurze Frist beginnt");
        Assert.IsTrue(h.ShouldStop(T0 + ServerHolds.QuickGrace, false));
    }

    [TestMethod]
    public void Outlook_DoesNotChangeTheHolds()
    {
        var h = new ServerHolds(T0, pid => pid == 7);
        h.Touch("ui:7", T0);
        h.Touch("tui:8", T0);                            // tot, aber noch eingetragen
        _ = h.Outlook(T0, "ui:7", 0, keepRunning: false, busy: false, proxyActive: 0);
        Assert.AreEqual("ui 1, tui 1", h.Describe(T0), "die Abfrage entfernt nichts");
    }
}
