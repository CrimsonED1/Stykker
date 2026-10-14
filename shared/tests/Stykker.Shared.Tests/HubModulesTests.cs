using Stykker.Shared.Config;

namespace Stykker.Shared.Tests;

public class HubModulesTests
{
    [Fact]
    public void Without_a_file_the_suite_is_listed_on_its_fixed_ports()
    {
        var modules = HubModules.Load(Path.Combine(Path.GetTempPath(), "stykker-missing-" + Guid.NewGuid().ToString("N"), "stykker.json"));

        Assert.Equal(["monitor", "sys", "llm"], modules.Select(m => m.Id));
        Assert.Equal(17401, modules.Single(m => m.Id == "llm").Port);
        Assert.Equal(17402, modules.Single(m => m.Id == "sys").Port);
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

    [Fact]
    public void A_file_without_the_new_fields_reads_as_before()
    {
        var modules = HubModules.Parse("""
            { "modules": [
                { "id": "llm", "name": "LLM", "port": 8078 },
                { "id": "qwen", "name": "Qwen Code", "url": "http://pc:8076", "external": true }
            ] }
            """);

        Assert.All(modules, m => Assert.Null(m.Target));
        Assert.All(modules, m => Assert.Null(m.Start));
        Assert.Equal(8078, modules[0].Port);
        Assert.Equal("http://pc:8076/", modules[1].Url);
        Assert.True(modules[1].External);
    }

    [Fact]
    public void A_complete_external_entry_keeps_its_target_and_start()
    {
        var module = Assert.Single(HubModules.Parse("""
            { "modules": [ { "id": "qwen", "name": "Qwen Code", "external": true, "port": 8076,
                "target": "http://127.0.0.1:41999",
                "start": { "command": "node", "args": ["C:\\x\\cli.js", "serve", "--port", "41999"],
                           "env": { "QWEN_HOME": "C:\\x\\home" }, "tokenEnv": "QWEN_SERVER_TOKEN" } } ] }
            """));

        Assert.Equal(8076, module.Port);
        Assert.Equal("http://127.0.0.1:41999", module.Target);
        Assert.Equal("node", module.Start!.Command);
        Assert.Equal(["C:\\x\\cli.js", "serve", "--port", "41999"], module.Start.Args);
        Assert.Equal("C:\\x\\home", module.Start.Env["QWEN_HOME"]);
        Assert.Equal("QWEN_SERVER_TOKEN", module.Start.TokenEnv);
        Assert.Equal("http://pc:8076/", module.AddressFor("http", "pc"));
    }

    [Fact]
    public void A_target_on_another_machine_is_dropped_and_the_entry_stays()
    {
        var module = Assert.Single(HubModules.Parse("""
            { "modules": [ { "id": "qwen", "external": true, "port": 8076, "target": "http://192.168.0.5:41999",
                "start": { "command": "node", "tokenEnv": "QWEN_SERVER_TOKEN" } } ] }
            """));

        Assert.Equal(8076, module.Port);
        Assert.Null(module.Target);
        Assert.Null(module.Start);
    }

    [Theory]
    [InlineData("http://127.0.0.1:41999", "http://127.0.0.1:41999")]
    [InlineData("http://127.0.0.1:41999/", "http://127.0.0.1:41999")]
    [InlineData("http://localhost:41999", "http://localhost:41999")]
    [InlineData("https://127.0.0.1:41999", null)]
    [InlineData("http://127.0.0.1", null)]
    [InlineData("http://127.0.0.1:0", null)]
    [InlineData("http://127.0.0.1:70000", null)]
    [InlineData("http://127.0.0.1:41999/path", null)]
    [InlineData("http://127.0.0.1:41999?x=1", null)]
    [InlineData("http://user@127.0.0.1:41999", null)]
    [InlineData("http://127.0.0.1.nip.io:41999", null)]
    [InlineData("http://[::1]:41999", null)]
    [InlineData("", null)]
    public void Only_the_local_target_forms_are_accepted(string target, string? expected) =>
        Assert.Equal(expected, HubModules.LocalTarget(target));

    [Fact]
    public void A_target_without_a_port_on_an_internal_entry_is_ignored()
    {
        var module = Assert.Single(HubModules.Parse("""
            { "modules": [ { "id": "llm", "port": 8078, "target": "http://127.0.0.1:41999",
                "start": { "command": "node", "tokenEnv": "X" } } ] }
            """));

        Assert.Null(module.Target);
        Assert.Null(module.Start);
    }

    [Theory]
    [InlineData("""{ "command": "", "tokenEnv": "QWEN_SERVER_TOKEN" }""")]
    [InlineData("""{ "tokenEnv": "QWEN_SERVER_TOKEN" }""")]
    [InlineData("""{ "command": "node" }""")]
    [InlineData("""{ "command": "node", "tokenEnv": "not a name" }""")]
    [InlineData("""{ "command": "node", "tokenEnv": "QWEN_SERVER_TOKEN", "args": "serve" }""")]
    [InlineData("""{ "command": "node", "tokenEnv": "QWEN_SERVER_TOKEN", "args": ["serve", 41999] }""")]
    [InlineData("""{ "command": "node", "tokenEnv": "QWEN_SERVER_TOKEN", "env": { "QWEN_HOME": 1 } }""")]
    [InlineData("""{ "command": "node", "tokenEnv": "QWEN_SERVER_TOKEN", "env": { "bad name": "x" } }""")]
    public void A_broken_start_leaves_out_only_the_start(string start)
    {
        var module = Assert.Single(HubModules.Parse($$"""
            { "modules": [ { "id": "qwen", "external": true, "port": 8076, "target": "http://127.0.0.1:41999", "start": {{start}} } ] }
            """));

        Assert.Equal(8076, module.Port);
        Assert.Equal("http://127.0.0.1:41999", module.Target);
        Assert.Null(module.Start);
    }

    [Fact]
    public void A_forwarded_entry_with_a_token_file_keeps_its_target_and_the_expanded_path()
    {
        var module = Assert.Single(HubModules.Parse("""
            { "modules": [ { "id": "qwen", "name": "Qwen Code", "external": true, "port": 17450, "target": "http://127.0.0.1:16000",
                "tokenFile": { "path": "%APPDATA%\\com.alibaba.qwen-code\\daemon-token.bin", "protection": "dpapi-user:qwen-desktop.daemon-token.v1" } } ] }
            """));

        Assert.Equal(17450, module.Port);
        Assert.Equal("http://127.0.0.1:16000", module.Target);
        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%APPDATA%\com.alibaba.qwen-code\daemon-token.bin"), module.TokenFile!.Path);
        Assert.Equal("dpapi-user:qwen-desktop.daemon-token.v1", module.TokenFile.Protection);
    }

    [Fact]
    public void A_token_file_with_an_unknown_protection_is_left_out_and_the_entry_stays()
    {
        var module = Assert.Single(HubModules.Parse("""
            { "modules": [ { "id": "qwen", "external": true, "port": 17450, "target": "http://127.0.0.1:16000",
                "tokenFile": { "path": "C:\\x\\token.bin", "protection": "plain" } } ] }
            """));

        Assert.Equal("http://127.0.0.1:16000", module.Target);
        Assert.Null(module.TokenFile);
    }

    [Fact]
    public void A_token_file_without_a_path_or_on_an_internal_entry_is_left_out()
    {
        var modules = HubModules.Parse("""
            { "modules": [
                { "id": "qwen", "external": true, "port": 17450, "target": "http://127.0.0.1:16000", "tokenFile": { "path": "  ", "protection": "none" } },
                { "id": "qwen2", "external": true, "port": 17452, "target": "http://127.0.0.1:16001", "tokenFile": "token.bin" },
                { "id": "llm", "port": 17401, "tokenFile": { "path": "C:\\x\\token.bin", "protection": "none" } }
            ] }
            """);

        Assert.Equal(["qwen", "qwen2", "llm"], modules.Select(m => m.Id));
        Assert.All(modules, m => Assert.Null(m.TokenFile));
    }

    [Fact]
    public void A_token_file_without_a_forwarding_target_is_not_taken()
    {
        var modules = HubModules.Parse("""
            { "modules": [
                { "id": "qwen", "external": true, "port": 17450, "tokenFile": { "path": "C:\\x\\token.bin", "protection": "none" } },
                { "id": "other", "external": true, "port": 17452, "target": "http://192.0.2.10:16000", "tokenFile": { "path": "C:\\x\\token.bin", "protection": "none" } }
            ] }
            """);

        Assert.All(modules, m => Assert.Null(m.TokenFile));
        Assert.All(modules, m => Assert.Null(m.Target));
    }
}
