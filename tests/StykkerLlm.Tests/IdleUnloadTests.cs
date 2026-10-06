using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Leerlauf-Entladen: nach eingestellten Minuten ohne Anfrage wird der Server gestoppt (llama.cpp)
// bzw. das Modell entladen (Ollama, LM Studio). Opt-in je Profil. Die Regel ist bewusst zustandsrein
// (Zeiten statt Server), damit sie sich ohne Simulator prüfen lässt; ein Test unten fährt den echten Weg
// über die Engine.
[TestClass]
public class IdleUnloadTests
{
    private static readonly DateTime Jetzt = new(2026, 10, 5, 14, 0, 0, DateTimeKind.Local);

    private static Profile Profil(int min) => new()
    {
        Name = "mein Server", Program = @"C:\llama\llama-server.exe", Args = { "-m", "C:\\models\\a.gguf", "--port", "8090" },
        UnloadAfterIdleMin = min,
    };

    // lastBusyAt = null heißt: der Server hat noch nie gearbeitet, dann zählt "since".
    private static bool Faellig(int min, bool online = true, bool busy = false, int? idleMin = 20, DateTime? since = null) =>
        IdleUnload.Due(Profil(min), online, busy, Jetzt.AddMinutes(-(idleMin ?? 0)), since ?? Jetzt, Jetzt);

    [TestMethod]
    public void Aus_Standardmaessig_UndEinAusgeschaltetesProfilEntlaedtNie()
    {
        Assert.AreEqual(0, IdleUnload.MinutesOf(null));
        Assert.IsFalse(Faellig(0, idleMin: 600), "Vorgabe ist aus");
        Assert.IsFalse(IdleUnload.Due(null, true, false, Jetzt.AddHours(-5), Jetzt, Jetzt), "ohne Profil nichts");
    }

    [TestMethod]
    public void NachDerEingestelltenZeit_WirdEntladen_DavorNicht()
    {
        Assert.IsFalse(Faellig(10, idleMin: 9), "9 von 10 Minuten: zu früh");
        Assert.IsTrue(Faellig(10, idleMin: 10), "genau 10 Minuten: fällig");
        Assert.IsTrue(Faellig(10, idleMin: 45), "45 Minuten: fällig");
    }

    [TestMethod]
    public void EinLaufenderOderOfflineServerWirdNieEntladen()
    {
        Assert.IsFalse(Faellig(1, busy: true, idleMin: 600), "mitten in einer Anfrage wird nie entladen");
        Assert.IsFalse(Faellig(1, online: false, idleMin: 600), "ein nicht erreichbarer Server wird nicht entladen");
    }

    [TestMethod]
    public void OhneArbeit_GiltDieZeitSeitDerBeobachtung()
    {
        // lastBusyAt = null: der Server lief die ganze Zeit untätig, dann zählt "since" (Beginn der Beobachtung)
        Assert.IsFalse(Faellig(30, idleMin: 0), "gerade gesehen: noch nicht");
        Assert.IsTrue(IdleUnload.Due(Profil(5), true, false, null, Jetzt.AddMinutes(-6), Jetzt), "seit 6 min ohne Arbeit");
        Assert.IsFalse(IdleUnload.Due(Profil(5), true, false, null, Jetzt.AddMinutes(-4), Jetzt));
    }

    [TestMethod]
    public void GrenzenTerminUndTexte()
    {
        Assert.AreEqual(30, IdleUnload.MinutesOf(Profil(30)));
        Assert.AreEqual(720, IdleUnload.MinutesOf(Profil(9999)), "höchstens 12 Stunden");
        Assert.AreEqual(0, IdleUnload.MinutesOf(Profil(-5)), "negative Werte bedeuten aus");

        Assert.AreEqual(Jetzt.AddMinutes(15), IdleUnload.DueAt(Profil(20), Jetzt.AddMinutes(-5), Jetzt));
        Assert.AreEqual(Jetzt.AddMinutes(20), IdleUnload.DueAt(Profil(20), null, Jetzt), "ohne Arbeit seit Beginn");
        Assert.IsNull(IdleUnload.DueAt(Profil(0), Jetzt, Jetzt), "ohne Einstellung gibt es keinen Termin");

        StringAssert.Contains(Strings.IdleUnloadSet(20), "20");
        StringAssert.Contains(Strings.IdleUnloadHint, "Ollama");
        StringAssert.Contains(Strings.IdleUnloadDone("x"), "x");
    }

    // Der echte Weg: die Engine merkt sich die Arbeitszeit und meldet nur, was das Profil verlangt.
    [TestMethod]
    public async Task DieEngineMeldetNurFelligeServer()
    {
        var spec = new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "sim-a", Model = "m", Context = 4096, Slots = 1, TpsMin = 50, TpsMax = 70 };
        using var host = new SimHost(new List<SimServerSpec> { spec }, seed: 6, autoStep: false);
        host.World.Start();
        var gemeldet = new List<string>();
        host.Engine.IdleUnloadDue += (_, p) => gemeldet.Add(p.Name);

        for (int i = 0; i < 4; i++)
        {
            host.World.Step(DateTime.Now.AddSeconds(5));
            await host.Engine.Registry.RefreshNowAsync();
            await host.Engine.TickAsync();
        }
        var w = host.Engine.Servers.FirstOrDefault(s => s.Name == "sim-a");
        Assert.IsNotNull(w, "der simulierte Server wird erkannt");
        Assert.AreEqual(0, gemeldet.Count, "ohne Profil will es niemand");

        var profil = Profil(10);
        // Das Profil muss zu dem Server passen, den die Simulation meldet – der Schlüssel ist Programm + Argumente
        var angelegt = host.Engine.Library.AddProfile(
            new HistoryEntry { Key = w!.ProfileKey, Program = w.Info.Program ?? "", Args = w.Info.Args.ToList() }, profil.Name, DateTime.Now);
        Assert.IsNotNull(angelegt, "das Profil kam in die Bibliothek");
        angelegt!.UnloadAfterIdleMin = 10;

        // 20 Minuten „lang" machen: die Zeit der Engine kommt aus _now, also hier die Zeitpunkte setzen
        w.MarkBusy(DateTime.Now.AddMinutes(-20));
        await host.Engine.TickAsync();
        Assert.IsTrue(gemeldet.Contains(profil.Name), "jetzt wird gemeldet: " + string.Join(",", gemeldet));
    }
}