using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Die gemerkte Wahl beim Schließen, und wann trotzdem gefragt wird (laufende Anfrage über den Proxy)
[TestClass]
public class CloseRuleTests
{
    private static ShutdownHint Hint(int proxyActive) => new("Quitting shuts the server down.", null, proxyActive);

    [TestMethod]
    public void Decide_RememberedChoice_AppliesWithoutARequest()
    {
        Assert.AreEqual(CloseRule.Tray, CloseRule.Decide(CloseRule.Tray, Hint(0)));
        Assert.AreEqual(CloseRule.Quit, CloseRule.Decide(CloseRule.Quit, Hint(0)));
        Assert.AreEqual(CloseRule.Quit, CloseRule.Decide(CloseRule.Quit, null), "ohne Antwort des Servers gilt die Wahl");
    }

    [TestMethod]
    public void Decide_ProxyRequestRunning_AsksEvenIfRemembered()
    {
        Assert.AreEqual(CloseRule.Ask, CloseRule.Decide(CloseRule.Tray, Hint(1)));
        Assert.AreEqual(CloseRule.Ask, CloseRule.Decide(CloseRule.Quit, Hint(2)));
    }

    [TestMethod]
    public void Decide_NothingRemembered_Asks()
    {
        Assert.AreEqual(CloseRule.Ask, CloseRule.Decide(CloseRule.Ask, null));
        Assert.AreEqual(CloseRule.Ask, CloseRule.Decide("unbekannt", null));
    }
}
