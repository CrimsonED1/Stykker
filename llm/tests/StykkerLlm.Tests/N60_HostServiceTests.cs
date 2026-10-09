using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// StykkerHost als Windows-Dienst (docs/plan-hosts-gateway.md, P8): Kopplung über Anfragedateien, Befehle für sc.exe
[TestClass]
public class N60_HostServiceTests
{
    private static AppPaths Temp() => new(Path.Combine(Path.GetTempPath(), "slm-n60-" + Guid.NewGuid().ToString("N")));

    [TestMethod, Timeout(10000)]
    public async Task Request_IsTakenOnce_AndTheResultComesBack()
    {
        var paths = Temp();
        Assert.IsNull(HostServiceFiles.TakeRequest(paths));
        HostServiceFiles.WriteRequest(paths, new HostServiceFiles.Request("http://pc:8078", "123456"));
        var req = HostServiceFiles.TakeRequest(paths);
        Assert.AreEqual("http://pc:8078", req!.Server);
        Assert.AreEqual("123456", req.Code);
        Assert.IsFalse(req.Unpair);
        Assert.IsNull(HostServiceFiles.TakeRequest(paths), "der Code gilt nur einmal: die Anfrage ist weg");

        var waiting = HostServiceFiles.WaitForResultAsync(paths, TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        HostServiceFiles.WriteResult(paths, new HostServiceFiles.Result(true, "paired"));
        var r = await waiting;
        Assert.IsTrue(r!.Ok);
        Assert.AreEqual("paired", r.Message);
        Assert.IsNull(await HostServiceFiles.WaitForResultAsync(paths, TimeSpan.FromMilliseconds(300)), "das Ergebnis wird nur einmal gelesen");
    }

    [TestMethod]
    public void InstallCommands_QuoteThePath_StartWithWindows_AndRestartAfterACrash()
    {
        var steps = HostServiceFiles.InstallCommands(@"C:\Program Files\Stykker\StykkerHost.exe");
        var create = steps[0];
        Assert.AreEqual("create", create[0]);
        CollectionAssert.Contains(create, "\"C:\\Program Files\\Stykker\\StykkerHost.exe\" --service");
        CollectionAssert.Contains(create, "delayed-auto");
        Assert.IsTrue(steps.Any(s => s[0] == "failure" && s.Any(a => a.StartsWith("restart/", StringComparison.Ordinal))));
        Assert.AreEqual("start", steps[^1][0]);
        CollectionAssert.AreEqual(new[] { "stop", "delete" }, HostServiceFiles.UninstallCommands().Select(s => s[0]).ToArray());
    }

    [TestMethod]
    public void SeedSettings_TakesTheUsersModelFolders_AndKeepsExistingOnes()
    {
        var user = Temp();
        var service = Temp();
        Directory.CreateDirectory(user.Root);
        File.WriteAllText(Path.Combine(user.Root, HostSettings.FileName), "{\"ModelRoots\":[\"D:\\\\models\"]}");
        HostServiceFiles.SeedSettings(user, service);
        CollectionAssert.AreEqual(new[] { @"D:\models" }, HostSettings.Load(service).ModelRoots);

        File.WriteAllText(Path.Combine(user.Root, HostSettings.FileName), "{\"ModelRoots\":[\"E:\\\\other\"]}");
        HostServiceFiles.SeedSettings(user, service);
        CollectionAssert.AreEqual(new[] { @"D:\models" }, HostSettings.Load(service).ModelRoots, "vorhandene Einstellungen des Dienstes bleiben");
    }
}
