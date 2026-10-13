using Stykker.Shared.Config;

namespace Stykker.Shared.Tests;

public class HubModulesTests
{
    [Fact]
    public void Without_a_file_the_suite_is_listed_on_its_fixed_ports()
    {
        var modules = HubModules.Load(Path.Combine(Path.GetTempPath(), "stykker-missing-" + Guid.NewGuid().ToString("N"), "stykker.json"));

        Assert.Equal(["monitor", "sys", "llm"], modules.Select(m => m.Id));
        Assert.Equal(8078, modules.Single(m => m.Id == "llm").Port);
    }

    [Fact]
    public void Own_modules_use_the_host_name_of_the_request()
    {
        var llm = new HubModule("llm", "LLM", 8078, null, false);

        Assert.Equal("http://workstation:8078/", llm.AddressFor("http", "workstation"));
        Assert.Equal("https://192.168.0.5:8078/", llm.AddressFor("https", "192.168.0.5"));
    }

    [Fact]
    public void The_monitor_is_the_hub_itself_and_an_external_service_keeps_its_address()
    {
        var modules = HubModules.Parse("""
            { "modules": [
                { "id": "monitor" },
                { "id": "Qwen", "name": "Qwen Code", "url": "http://pc:8076", "external": true }
            ] }
            """);

        Assert.Null(modules[0].AddressFor("http", "pc"));
        Assert.Equal("Monitor".ToUpperInvariant(), modules[0].Name);
        Assert.Equal("qwen", modules[1].Id);
        Assert.True(modules[1].External);
        Assert.Equal("http://pc:8076/", modules[1].AddressFor("http", "other-name"));
    }

    [Fact]
    public void Entries_that_cannot_be_shown_are_left_out()
    {
        var modules = HubModules.Parse("""
            { "modules": [
                { "id": "ok", "port": 8100 },
                { "id": "ok", "port": 8101 },
                { "id": "no address" , "port": 8102 },
                { "id": "noport" },
                { "id": "badport", "port": 70000 },
                { "id": "script", "url": "javascript:alert(1)" },
                { "id": "file", "url": "file:///C:/x" },
                "not an object",
                { "name": "no id", "port": 8103 }
            ] }
            """);

        Assert.Equal(["ok"], modules.Select(m => m.Id));
        Assert.Equal(8100, modules[0].Port);
    }

    [Fact]
    public void A_damaged_or_empty_file_falls_back_to_the_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-hub-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "stykker.json");
            File.WriteAllText(file, "{ not json");
            Assert.Same(HubModules.Defaults, HubModules.Load(file));

            File.WriteAllText(file, """{ "modules": [] }""");
            Assert.Same(HubModules.Defaults, HubModules.Load(file));

            File.WriteAllText(file, """{ "modules": [ { "id": "llm", "port": 9000 } ] } // comment""");
            Assert.Equal(9000, Assert.Single(HubModules.Load(file)).Port);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
