namespace Stykker.Shared.Net;

// Netzwerk über alle aktiven Adapter (ohne Loopback und Tunnel): Empfangen und Senden in Byte je Sekunde.
// LinkBytesPerSec = Summe der Verbindungsgeschwindigkeiten (0 = unbekannt), der Maßstab für einen Balken.
// Gebraucht von StykkerHUD (Durchsatz) und StykkerLLM (Hardware-Minibar).
public sealed record NetSample(double RxBytesPerSec, double TxBytesPerSec, double LinkBytesPerSec);

// Die Zählerstände der Adapter seit ihrem Start (Byte) und die Summe ihrer Verbindungsgeschwindigkeiten (Byte/s)
public readonly record struct NetTotals(long RxBytes, long TxBytes, double LinkBytesPerSec);

// Macht aus zwei Zählerständen einen Durchsatz je Sekunde. Der erste Aufruf setzt nur die Basis. Läuft ein Zähler
// rückwärts (Adapter weg, Zähler neu), beginnt die Messung neu, statt eine negative oder riesige Zahl zu melden.
// Kommt ein Aufruf zu kurz nach dem letzten, bleibt die Basis stehen: die nächste Rate misst dann über die längere Zeit.
public sealed class NetRateMeter
{
    public const double MinSeconds = 0.05;
    private long _rx = -1, _tx = -1;
    private DateTime _at;

    public NetSample? Next(NetTotals totals, DateTime now)
    {
        double sec = (now - _at).TotalSeconds;
        if (_rx >= 0 && sec >= 0 && sec < MinSeconds) return null;
        NetSample? result = null;
        if (_rx >= 0 && sec >= MinSeconds && totals.RxBytes >= _rx && totals.TxBytes >= _tx)
            result = new NetSample((totals.RxBytes - _rx) / sec, (totals.TxBytes - _tx) / sec, Math.Max(0, totals.LinkBytesPerSec));
        _rx = totals.RxBytes;
        _tx = totals.TxBytes;
        _at = now;
        return result;
    }

    public void Reset() => _rx = _tx = -1;
}
