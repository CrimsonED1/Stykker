namespace StykkerLlm.Core;

// Macht aus den Zählerständen der Netzwerkadapter (Byte seit dem Start) einen Durchsatz je Sekunde.
// Der erste Aufruf setzt nur die Basis. Läuft ein Zähler rückwärts (Adapter weg, Zähler übergelaufen),
// beginnt die Messung neu, statt eine negative oder riesige Zahl zu melden.
public sealed class NetRateMeter
{
    private long _rx = -1, _tx = -1;
    private DateTime _at;

    public NetSample? Next(long rxTotal, long txTotal, double linkBytesPerSec, DateTime now)
    {
        NetSample? result = null;
        double sec = (now - _at).TotalSeconds;
        if (_rx >= 0 && sec > 0.2 && rxTotal >= _rx && txTotal >= _tx)
            result = new NetSample((rxTotal - _rx) / sec, (txTotal - _tx) / sec, Math.Max(0, linkBytesPerSec));
        _rx = rxTotal;
        _tx = txTotal;
        _at = now;
        return result;
    }

    public void Reset() => _rx = _tx = -1;
}
