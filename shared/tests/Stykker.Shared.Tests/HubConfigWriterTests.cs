using System.Text.Json;
using Stykker.Shared.Config;

namespace Stykker.Shared.Tests;

// Die Dienste in stykker.json schreiben: die übrigen Einträge und Felder bleiben, die Datei wird atomar ersetzt, und eine
// kaputte Datei bleibt, wie sie ist. Adressen und Tokens sind Platzhalter.
public class HubConfigWriterTests : IDisposable
{
    private const int HubPort = 17400;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stykker-writer-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "stykker.json");

    public HubConfigWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Existing = """
        {
          "hostNames": ["pc.example.ts.net"],
          "extra": { "keep": [1, 2, 3] },
          "modules": [
            { "id": "llm", "name": "LLM", "port": 17401 },
            { "id": "sys", "name": "SYS", "port": 17402 },
            { "id": "qwen", "name": "Qwen Code", "external": true, "port": 17450,
              "target": "http://192.0.2.10:16000", "token": "placeholder-token-old" }
          ]
        }
        """;

    private JsonElement Root() => JsonDocument.Parse(File.ReadAllText(File_)).RootElement;

    private static ServiceEntry Entry(string id = "tail", int port = 17451) =>
        new(id, "Tail service", port, "https://pc.example.ts.net:16001", "placeholder-token-new");

    [Fact]
    public void Adding_a_service_keeps_every_other_entry_and_field()
    {
        File.WriteAllText(File_, Existing);

        var result = HubConfigWriter.Add(File_, Entry(), HubPort);

        Assert.True(result.Ok);
        var root = Root();
        Assert.Equal("keep", root.GetProperty("extra").EnumerateObject().Single().Name);
        Assert.Equal("pc.example.ts.net", root.GetProperty("hostNames")[0].GetString());
        Assert.Equal(["llm", "sys", "qwen", "tail"], root.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        var tail = root.GetProperty("modules")[3];
        Assert.True(tail.GetProperty("external").GetBoolean());
        Assert.Equal(17451, tail.GetProperty("port").GetInt32());
        Assert.Equal("https://pc.example.ts.net:16001", tail.GetProperty("target").GetString());
        Assert.Equal("placeholder-token-new", tail.GetProperty("token").GetString());
        Assert.Equal("placeholder-token-old", root.GetProperty("modules")[2].GetProperty("token").GetString());
    }

    [Fact]
    public void Adding_to_a_missing_file_creates_it_with_a_module_list()
    {
        var result = HubConfigWriter.Add(File_, Entry(), HubPort);

        Assert.True(result.Ok);
        Assert.Equal("tail", Root().GetProperty("modules")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("tail", 17400)]                       // der Hub selbst
    [InlineData("tail", 17401)]                       // LLM
    [InlineData("tail", 17402)]                       // SYS
    [InlineData("tail", 17450)]                       // schon von qwen belegt
    [InlineData("qwen", 17451)]                       // Id schon da
    [InlineData("llm", 17451)]                        // feste Id
    [InlineData("sys", 17451)]
    [InlineData("monitor", 17451)]
    [InlineData("Tail", 17451)]                       // Großschreibung
    [InlineData("tail_x", 17451)]
    [InlineData("", 17451)]
    [InlineData("tail", 0)]
    [InlineData("tail", 70000)]
    public void A_new_service_with_a_taken_or_invalid_id_or_port_is_refused_and_nothing_changes(string id, int port)
    {
        File.WriteAllText(File_, Existing);

        var result = HubConfigWriter.Add(File_, new ServiceEntry(id, "X", port, "http://127.0.0.1:16001", "t"), HubPort);

        Assert.False(result.Ok);
        Assert.Equal(Existing.Replace("\r\n", "\n"), File.ReadAllText(File_).Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_name_is_needed()
    {
        File.WriteAllText(File_, Existing);

        Assert.False(HubConfigWriter.Add(File_, new ServiceEntry("tail", "   ", 17451, "http://127.0.0.1:16001", "t"), HubPort).Ok);
    }

    [Fact]
    public void Check_says_what_add_would_refuse_without_writing()
    {
        File.WriteAllText(File_, Existing);
        var before = File.ReadAllText(File_);

        Assert.True(HubConfigWriter.Check(File_, "tail", "Tail", 17451, HubPort).Ok);
        Assert.False(HubConfigWriter.Check(File_, "tail", "Tail", 17450, HubPort).Ok);
        Assert.Equal(before, File.ReadAllText(File_));
    }

    [Fact]
    public void Replacing_the_url_changes_target_and_token_and_keeps_id_name_and_port()
    {
        File.WriteAllText(File_, Existing);

        var result = HubConfigWriter.Replace(File_, "qwen", "http://127.0.0.1:16009", "placeholder-token-newer");

        Assert.True(result.Ok);
        var qwen = Root().GetProperty("modules")[2];
        Assert.Equal("qwen", qwen.GetProperty("id").GetString());
        Assert.Equal("Qwen Code", qwen.GetProperty("name").GetString());
        Assert.Equal(17450, qwen.GetProperty("port").GetInt32());
        Assert.Equal("http://127.0.0.1:16009", qwen.GetProperty("target").GetString());
        Assert.Equal("placeholder-token-newer", qwen.GetProperty("token").GetString());
    }

    [Fact]
    public void Replacing_without_a_token_removes_the_token_field()
    {
        File.WriteAllText(File_, Existing);

        Assert.True(HubConfigWriter.Replace(File_, "qwen", "http://127.0.0.1:16009", null).Ok);

        Assert.False(Root().GetProperty("modules")[2].TryGetProperty("token", out _));
    }

    [Fact]
    public void Replacing_an_unknown_or_internal_service_is_refused()
    {
        File.WriteAllText(File_, Existing);

        Assert.False(HubConfigWriter.Replace(File_, "nope", "http://127.0.0.1:16009", "t").Ok);
        Assert.False(HubConfigWriter.Replace(File_, "llm", "http://127.0.0.1:16009", "t").Ok);
    }

    [Fact]
    public void Removing_a_service_takes_only_that_entry()
    {
        File.WriteAllText(File_, Existing);

        Assert.True(HubConfigWriter.Remove(File_, "qwen").Ok);

        var root = Root();
        Assert.Equal(["llm", "sys"], root.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        Assert.Equal("pc.example.ts.net", root.GetProperty("hostNames")[0].GetString());
        Assert.False(HubConfigWriter.Remove(File_, "qwen").Ok);
    }

    [Fact]
    public void A_file_that_is_not_json_is_never_overwritten()
    {
        File.WriteAllText(File_, "{ this is not json");

        Assert.False(HubConfigWriter.Add(File_, Entry(), HubPort).Ok);
        Assert.False(HubConfigWriter.Replace(File_, "qwen", "http://127.0.0.1:16009", "t").Ok);
        Assert.False(HubConfigWriter.Remove(File_, "qwen").Ok);
        Assert.Equal("{ this is not json", File.ReadAllText(File_));
    }

    [Fact]
    public void Modules_that_are_not_a_list_are_never_overwritten()
    {
        File.WriteAllText(File_, """{ "modules": { "id": "qwen" } }""");

        Assert.False(HubConfigWriter.Add(File_, Entry(), HubPort).Ok);
        Assert.Equal("""{ "modules": { "id": "qwen" } }""", File.ReadAllText(File_));
    }

    [Fact]
    public void The_write_leaves_no_temporary_file_behind()
    {
        File.WriteAllText(File_, Existing);

        HubConfigWriter.Add(File_, Entry(), HubPort);
        HubConfigWriter.Replace(File_, "tail", "http://127.0.0.1:16009", "t");
        HubConfigWriter.Remove(File_, "tail");

        Assert.Equal([File_], Directory.GetFiles(_dir));
    }

    [Fact]
    public void The_list_shows_the_services_with_a_flag_for_the_token_and_never_the_token()
    {
        File.WriteAllText(File_, Existing);

        var list = HubConfigWriter.List(File_);

        var qwen = Assert.Single(list);
        Assert.Equal("qwen", qwen.Id);
        Assert.Equal(17450, qwen.Port);
        Assert.Equal("http://192.0.2.10:16000", qwen.Target);
        Assert.True(qwen.TokenSet);
        // Das Listenelement hat kein Feld für das Token, auch nicht im Text
        Assert.Equal(["Id", "Name", "Port", "Target", "TokenSet"], typeof(ServiceListing).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("placeholder-token-old", qwen.ToString());
    }

    [Fact]
    public void A_service_without_a_token_is_listed_with_no_token()
    {
        File.WriteAllText(File_, """{ "modules": [ { "id": "x", "external": true, "port": 17450, "target": "http://127.0.0.1:16000" } ] }""");

        var listed = Assert.Single(HubConfigWriter.List(File_));

        Assert.False(listed.TokenSet);
        Assert.Null(HubConfigWriter.StoredToken(File_, "x"));
    }

    [Fact]
    public void The_stored_token_is_there_for_a_replace_and_missing_for_an_unknown_id()
    {
        File.WriteAllText(File_, Existing);

        Assert.Equal("placeholder-token-old", HubConfigWriter.StoredToken(File_, "qwen"));
        Assert.Null(HubConfigWriter.StoredToken(File_, "nope"));
    }

    [Fact]
    public void A_missing_or_broken_file_lists_nothing()
    {
        Assert.Empty(HubConfigWriter.List(File_));
        File.WriteAllText(File_, "{ broken");
        Assert.Empty(HubConfigWriter.List(File_));
    }
}
