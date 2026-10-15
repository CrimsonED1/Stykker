using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N82 – Feinschliff der Oberfläche: Zähler der Wege („Routed“ in der Proxy-Karte), Ziel eines Modelltests je Test,
// die Reiter des Proxy-Dialogs und der Text der Zeile
[TestClass]
public class N82_ProxyPanelTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0);

    private static ProxyRecord Rec(string serverKey, string path = "/v1/chat/completions", string? route = null) =>
        new(serverKey, path, T0, T0.AddSeconds(2), null, null, 0, 10, Array.Empty<string>(), "stop", "claude-cli", true, 200, false,
            Route: route);

    private const string CloudKey = "provider:https://cloud.example/v1|big";

    // ── Zähler der Wege ──

    [TestMethod]
    public void A_counts_start_at_zero()
    {
        var counts = new ProxyRouteCounts();
        Assert.AreEqual((0, 0, 0), counts.Snapshot());
    }

    [TestMethod]
    public void B_classifier_counts_as_auto_and_vision_as_image()
    {
        var counts = new ProxyRouteCounts();
        counts.Add(Rec("local|a", route: ProxyRoutes.Classifier));
        counts.Add(Rec("local|a", route: ProxyRoutes.Classifier));
        counts.Add(Rec("local|a", route: ProxyRoutes.Vision));
        counts.Add(Rec("local|a", route: ProxyRoutes.Default));
        counts.Add(Rec("local|a", route: ProxyRoutes.Named));
        Assert.AreEqual((2, 1, 0), counts.Snapshot());
    }

    [TestMethod]
    public void C_cloud_target_counts_as_cloud_whatever_the_route()
    {
        var counts = new ProxyRouteCounts();
        counts.Add(Rec(CloudKey, route: ProxyRoutes.Default));
        counts.Add(Rec(CloudKey, route: ProxyRoutes.Classifier));   // Sicherheitsprüfung in die Cloud: zwei Zähler
        Assert.AreEqual((1, 0, 2), counts.Snapshot());
    }

    [TestMethod]
    public void D_model_list_requests_are_not_counted()
    {
        var counts = new ProxyRouteCounts();
        counts.Add(Rec("local|a", path: "/v1/models", route: ProxyRoutes.Classifier));
        counts.Add(Rec(CloudKey, path: "/models", route: ProxyRoutes.Vision));
        Assert.AreEqual((0, 0, 0), counts.Snapshot());
    }

    [TestMethod]
    public void E_records_without_route_are_not_counted()
    {
        var counts = new ProxyRouteCounts();
        counts.Add(Rec("local|a", route: null));      // Antwort des Proxys selbst (z. B. offline)
        Assert.AreEqual((0, 0, 0), counts.Snapshot());
    }

    [TestMethod]
    public void F_text_of_the_line_names_each_route_with_its_count()
    {
        Assert.AreEqual($"{Strings.ReqRouteAuto} 3 · {Strings.ReqRouteImage} 1 · {Strings.ReqCloud} 0", Strings.PxRoutedText(3, 1, 0));
    }

    // ── Ziel eines Modelltests ──

    private static (string Url, bool HasKey)[] Providers(params (string Url, bool HasKey)[] list) => list;

    [TestMethod]
    public void G_test_target_is_the_chosen_provider_while_it_exists()
    {
        var list = Providers(("https://a/v1", true), ("https://b/v1", false));
        Assert.AreEqual("https://b/v1", ProxyPanel.TestTarget(list, "https://b/v1"));
    }

    [TestMethod]
    public void H_without_a_choice_the_first_provider_with_a_key_is_used()
    {
        var list = Providers(("https://a/v1", false), ("https://b/v1", true), ("https://c/v1", true));
        Assert.AreEqual("https://b/v1", ProxyPanel.TestTarget(list, null));
    }

    [TestMethod]
    public void I_without_a_key_anywhere_the_first_provider_is_used_and_an_unknown_choice_is_dropped()
    {
        var list = Providers(("https://a/v1", false), ("https://b/v1", false));
        Assert.AreEqual("https://a/v1", ProxyPanel.TestTarget(list, "https://gone/v1"));
    }

    [TestMethod]
    public void J_no_provider_gives_an_empty_target()
    {
        Assert.AreEqual("", ProxyPanel.TestTarget(Array.Empty<(string Url, bool HasKey)>(), "https://a/v1"));
    }

    // ── Reiter des Proxy-Dialogs ──

    [TestMethod]
    public void K_tabs_are_in_dialog_order_and_general_comes_first()
    {
        CollectionAssert.AreEqual(new[] { "general", "sources", "routing", "tests" }, ProxyDialogTabs.All);
    }

    [TestMethod]
    public void L_a_stored_tab_is_kept_and_anything_else_falls_back_to_general()
    {
        Assert.AreEqual("routing", ProxyDialogTabs.Parse("routing"));
        Assert.AreEqual("tests", ProxyDialogTabs.Parse("tests"));
        Assert.AreEqual(ProxyDialogTabs.General, ProxyDialogTabs.Parse(null));
        Assert.AreEqual(ProxyDialogTabs.General, ProxyDialogTabs.Parse(""));
        Assert.AreEqual(ProxyDialogTabs.General, ProxyDialogTabs.Parse("nope"));
    }

    // ── Beschriftung „Default model“ ──

    [TestMethod]
    public void M_the_default_model_label_and_its_tip_name_the_fallback_targets()
    {
        Assert.AreEqual("Default model", Strings.PxDefault);
        StringAssert.Contains(Strings.PxDefaultTip, "stykker");
        StringAssert.Contains(Strings.PxDefaultTip, "claude-stykker");
    }
}
