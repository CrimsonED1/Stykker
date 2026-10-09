using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Texte der Oberfläche. Die Prüfung lohnt sich, weil ein Zahl-Text wie "1 devices" beim Abrufen der Seite
// auffällt, aber beim Lesen des Codes nicht – der Fehler steckt im Zusammensetzen, nicht in einer Zahl.
[TestClass]
public class StringsUiTests
{
    [TestMethod]
    public void DeviceCount_IsSingularForExactlyOne()
    {
        Assert.AreEqual("1 device", Strings.RemoteDeviceCount(1));
    }

    [TestMethod]
    public void DeviceCount_IsPluralForNoneAndMany()
    {
        Assert.AreEqual("0 devices", Strings.RemoteDeviceCount(0));
        Assert.AreEqual("2 devices", Strings.RemoteDeviceCount(2));
    }

    [TestMethod]
    public void PhoneLink_IsNotNamedLikeTheCardAboveIt()
    {
        // Karte und Link hängen nebeneinander; gleicher Text liest sich wie ein doppelter Eintrag.
        Assert.AreNotEqual(Strings.RemoteTitle, Strings.RemoteOpenPage);
        Assert.IsTrue(Strings.RemoteOpenPage.Length > 0);
    }
}