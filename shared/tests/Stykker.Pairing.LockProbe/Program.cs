using Stykker.Pairing;

// Aufruf: LockProbe <pairing.dat> <Anzahl> <Präfix> [read <Modul> <Token>]
//   ohne "read": trägt <Anzahl> Geräte ein, jedes mit eigenem Schreibvorgang, und endet mit 0.
//   mit "read":  ein zweiter Prozess, der nur Stykker.Pairing kennt, fragt die Rolle eines Tokens ab und gibt sie aus.
if (args.Length >= 6 && args[3] == "read")
{
    var role = new PairingService(new PairingStore(args[0])).RoleOf(args[5], args[4]);
    Console.WriteLine(role ?? "none");
    return 0;
}

var store = new PairingStore(args[0]);
var service = new PairingService(store);
int count = int.Parse(args[1]);
for (int i = 0; i < count; i++)
    service.AddLocalDevice($"{args[2]}-{i}", "127.0.0.1", "probe");
return 0;
