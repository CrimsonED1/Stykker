using System.Diagnostics;
using Stykker.Pairing;

namespace Stykker.Pairing.Tests;

public sealed class PairingStoreTests : IDisposable
{
    private readonly TestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_missing_file_reads_as_empty_and_is_not_created_by_reading()
    {
        var state = _folder.Store().Read();

        Assert.Empty(state.Devices);
        Assert.False(File.Exists(_folder.File));
    }

    [Fact]
    public void Read_returns_a_copy()
    {
        var store = _folder.Store();
        store.Update(state => { state.Devices.Add(Device("a")); return true; });

        store.Read().Devices.Clear();

        Assert.Single(store.Read().Devices);
    }

    [Fact]
    public void Another_instance_sees_a_change_without_restart()
    {
        var first = _folder.Store();
        var second = _folder.Store();
        Assert.Empty(second.Read().Devices);

        first.Update(state => { state.Devices.Add(Device("a")); return true; });

        Assert.Single(second.Read().Devices);
    }

    [Fact]
    public void Update_reads_fresh_so_two_writers_do_not_overwrite_each_other()
    {
        var first = _folder.Store();
        var second = _folder.Store();
        first.Read();
        second.Read();

        first.Update(state => { state.Devices.Add(Device("a")); return true; });
        second.Update(state => { state.Devices.Add(Device("b")); return true; });

        Assert.Equal(2, first.Read().Devices.Count);
    }

    [Fact]
    public void Update_without_change_does_not_write()
    {
        var store = _folder.Store();
        int changed = 0;
        store.Changed += () => changed++;

        store.Update(_ => false);

        Assert.False(File.Exists(_folder.File));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Writing_leaves_no_temp_file_behind()
    {
        var store = _folder.Store();
        for (int i = 0; i < 20; i++)
            store.Update(state => { state.Devices.Add(Device("d" + i)); return true; });

        Assert.Equal([PairingStore.FileName], Directory.GetFiles(_folder.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void An_interrupted_write_does_not_damage_the_file()
    {
        var store = _folder.Store();
        store.Update(state => { state.Devices.Add(Device("a")); return true; });
        // Ein Absturz mitten im Schreiben hinterlässt eine halbe Temp-Datei neben der Ablage
        File.WriteAllText(_folder.File + ".deadbeef.tmp", "{ \"devices\": [ { \"id\": \"ha");

        Assert.Single(_folder.Store().Read().Devices);
        _folder.Store().Update(state => { state.Devices.Add(Device("b")); return true; });
        Assert.Equal(2, _folder.Store().Read().Devices.Count);
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_before_it_is_replaced()
    {
        File.WriteAllText(_folder.File, "this is neither json nor base64 !!");
        var store = _folder.Store();
        Assert.Empty(store.Read().Devices);

        store.Update(state => { state.Devices.Add(Device("a")); return true; });

        Assert.Single(store.Read().Devices);
        var kept = Assert.Single(Directory.GetFiles(_folder.Root, PairingStore.FileName + ".unreadable-*"));
        Assert.Equal("this is neither json nor base64 !!", File.ReadAllText(kept));
    }

    [Fact]
    public void Entries_without_id_or_hash_are_dropped_on_load()
    {
        File.WriteAllText(_folder.File,
            """{ "devices": [ { "id": "", "hash": "x" }, { "id": "a", "hash": "" }, { "id": "b", "hash": "h" } ] }""");

        Assert.Equal(["b"], _folder.Store().Read().Devices.Select(d => d.Id));
    }

    [Fact]
    public void Dpapi_binds_the_file_to_the_user()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new PairingStore(_folder.File, DataProtectors.ForCurrentUser());
        store.Update(state => { state.Devices.Add(Device("secret-device")); return true; });

        var raw = File.ReadAllText(_folder.File);
        Assert.DoesNotContain("secret-device", raw);
        Assert.False(raw.TrimStart().StartsWith('{'));

        Assert.Single(new PairingStore(_folder.File, DataProtectors.ForCurrentUser()).Read().Devices);
        // Ein anderer Zusatzwert (ein anderes Programm desselben Benutzers) liest sie nicht
        Assert.Empty(new PairingStore(_folder.File, DataProtectors.ForCurrentUser("something-else")).Read().Devices);
    }

    // Plan Phase 1: zwei Prozesse schreiben gleichzeitig, kein Eintrag geht verloren.
    [Fact]
    public async Task Two_processes_writing_at_once_lose_no_entry()
    {
        const int perProcess = 40;
        var probes = new[] { "left", "right", "third" }.Select(prefix => StartProbe(_folder.File, perProcess.ToString(), prefix)).ToList();

        foreach (var probe in probes)
        {
            await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.True(probe.ExitCode == 0, await probe.StandardError.ReadToEndAsync());
        }

        // Die Probe schreibt mit dem Schutz des Benutzers, wie ein echtes Modul
        var devices = new PairingStore(_folder.File).Read().Devices;
        Assert.Equal(3 * perProcess, devices.Count);
        Assert.Equal(3 * perProcess, devices.Select(d => d.Id).Distinct().Count());
        Assert.Equal(perProcess, devices.Count(d => d.Name.StartsWith("left-", StringComparison.Ordinal)));
    }

    // Plan, Akzeptanz 4: ein zweiter Prozess, der nur Stykker.Pairing nutzt, kennt das Gerät – als Viewer.
    [Fact]
    public async Task A_second_process_knows_a_device_paired_elsewhere()
    {
        var service = new PairingService(new PairingStore(_folder.File));
        var token = service.Pair(service.Code, "phone", null, "llm")!.Value.Token;

        var probe = StartProbe(_folder.File, "0", "x", "read", "sys", token);
        var output = (await probe.StandardOutput.ReadToEndAsync()).Trim();
        await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(PairingRoles.Viewer, output);
    }

    private static Process StartProbe(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Stykker.Pairing.LockProbe.dll");
        Assert.True(File.Exists(dll), $"Hilfsprogramm fehlt: {dll}");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(dll);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    private static PairedDevice Device(string id) => new() { Id = id, Name = id, Hash = "hash-" + id, Created = DateTime.UtcNow };
}
