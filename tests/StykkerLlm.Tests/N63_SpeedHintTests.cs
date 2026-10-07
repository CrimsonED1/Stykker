using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// „Langsamer als sonst“ (docs/plan-ui-redesign.md, U11)
[TestClass]
public class N63_SpeedHintTests
{
    [TestMethod]
    public void Hint_OnlyWithEnoughSamples_AndAClearDrop()
    {
        Assert.AreEqual(20, SpeedHint.SlowerPercent(40, 30, 50, 500));
        Assert.IsNull(SpeedHint.SlowerPercent(45, 30, 50, 500), "10 % ist noch Rauschen");
        Assert.IsNull(SpeedHint.SlowerPercent(60, 30, 50, 500), "schneller: kein Hinweis");
        Assert.IsNull(SpeedHint.SlowerPercent(20, 5, 50, 500), "zu wenige Takte in diesem Lauf");
        Assert.IsNull(SpeedHint.SlowerPercent(20, 30, 50, 10), "zu wenig Verlauf");
        Assert.IsNull(SpeedHint.SlowerPercent(20, 30, 0, 500));
    }
}
