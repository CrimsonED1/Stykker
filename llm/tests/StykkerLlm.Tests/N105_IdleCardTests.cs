using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Leerlauf auf der Serverkarte (IdleUnload.Idle, IdleInfo): wie lange ohne Anfrage, wann das Profil entlädt
[TestClass]
public class N105_IdleCardTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 7, 0, 0);
    private static readonly DateTime Since = Now.AddHours(-3);
    private static Profile WithUnload(int min) => new() { Name = "qwen", UnloadAfterIdleMin = min };

    [TestMethod]
    public void Idle_NotShown_WhenBusyOfflineOrShortPause()
    {
        Assert.IsNull(IdleUnload.Idle(null, online: true, busy: true, Now.AddMinutes(-30), Since, Now));
        Assert.IsNull(IdleUnload.Idle(null, online: false, busy: false, Now.AddMinutes(-30), Since, Now));
        Assert.IsNull(IdleUnload.Idle(null, online: true, busy: false, Now.AddSeconds(-90), Since, Now), "Pause zwischen Agent-Anfragen");
    }

    [TestMethod]
    public void Idle_WithoutProfileUnload_OnlyIdleTime()
    {
        var i = IdleUnload.Idle(null, true, false, Now.AddMinutes(-40), Since, Now)!;
        Assert.AreEqual(TimeSpan.FromMinutes(40), i.Idle);
        Assert.IsNull(i.UnloadIn);
        Assert.AreEqual("idle 40 min", i.Text);
        Assert.AreEqual(Strings.CardIdleTip, i.Tip);
        Assert.IsFalse(i.Soon);
    }

    [TestMethod]
    public void Idle_WithProfileUnload_CountsDown()
    {
        var i = IdleUnload.Idle(WithUnload(52), true, false, Now.AddMinutes(-40), Since, Now)!;
        Assert.AreEqual(TimeSpan.FromMinutes(12), i.UnloadIn);
        Assert.AreEqual("idle 40 min · unload in 12 min", i.Text);
        Assert.AreEqual(Strings.CardIdleUnloadTip(52), i.Tip);
        Assert.IsFalse(i.Soon);

        var soon = IdleUnload.Idle(WithUnload(41), true, false, Now.AddMinutes(-40), Since, Now)!;
        Assert.IsTrue(soon.Soon);
        var over = IdleUnload.Idle(WithUnload(30), true, false, Now.AddMinutes(-40), Since, Now)!;
        Assert.AreEqual(TimeSpan.Zero, over.UnloadIn, "überfällig: nie negativ");
    }

    [TestMethod]
    public void Idle_NeverBusySinceWatching_IsAtLeast()
    {
        var i = IdleUnload.Idle(null, true, false, null, Now.AddMinutes(-125), Now)!;
        Assert.IsTrue(i.AtLeast);
        Assert.AreEqual("idle ≥ 2 h 05 min", i.Text);
    }

    [TestMethod]
    public void Span_Format()
    {
        Assert.AreEqual("7 min", IdleUnload.Span(TimeSpan.FromMinutes(7.9)));
        Assert.AreEqual("1 h 00 min", IdleUnload.Span(TimeSpan.FromMinutes(60)));
    }
}
