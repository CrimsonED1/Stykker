using StykkerStarter.Core;

namespace StykkerStarter.Tests;

public class ShutdownTests
{
    [Fact]
    public void Steps_run_in_the_order_given_and_each_is_logged_when_done()
    {
        using var temp = new TempFolder();
        var file = temp.Sub("logs\\starter.log");
        var log = new StarterLog(file);
        var ran = new List<string>();

        Shutdown.Run(log, new (string, Action)[]
        {
            ("engine stopped", () => ran.Add("engine")),
            ("tray icon removed", () => ran.Add("tray")),
            ("process ends", () => ran.Add("end")),
        });

        Assert.Equal(new[] { "engine", "tray", "end" }, ran);
        var lines = File.ReadAllLines(file);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("quit: engine stopped", lines[0]);
        Assert.EndsWith("quit: process ends", lines[2]);
    }

    [Fact]
    public void A_step_that_throws_is_logged_and_the_rest_still_runs()
    {
        using var temp = new TempFolder();
        var file = temp.Sub("logs\\starter.log");
        var ran = new List<string>();

        Shutdown.Run(new StarterLog(file), new (string, Action)[]
        {
            ("tray icon removed", () => throw new InvalidOperationException("tray already gone")),
            ("queue stopped", () => ran.Add("queue")),
        });

        Assert.Equal(new[] { "queue" }, ran);
        var text = File.ReadAllText(file);
        Assert.Contains("quit: tray icon removed failed: InvalidOperationException: tray already gone", text);
        Assert.Contains("quit: queue stopped", text);
    }
}
