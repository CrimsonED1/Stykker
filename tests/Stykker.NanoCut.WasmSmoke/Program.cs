using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Stykker.NanoCut;
using Stykker.NanoCut.Testing;

Console.WriteLine($"Stykker.NanoCut wasm smoke test on {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
int failures = 0;

// Predicates against BigInteger.
var rng = new Random(1);
const long M = Units.MaxCoordinate;
var sw = Stopwatch.StartNew();
for (int i = 0; i < 20_000; i++)
{
    Vec3 a = R3(), b = R3(), c = R3(), d = R3();
    BigInteger bx = (BigInteger)b.X - a.X, by = (BigInteger)b.Y - a.Y, bz = (BigInteger)b.Z - a.Z;
    BigInteger cx = (BigInteger)c.X - a.X, cy = (BigInteger)c.Y - a.Y, cz = (BigInteger)c.Z - a.Z;
    BigInteger dx = (BigInteger)d.X - a.X, dy = (BigInteger)d.Y - a.Y, dz = (BigInteger)d.Z - a.Z;
    var exact = (by * cz - bz * cy) * dx + (bz * cx - bx * cz) * dy + (bx * cy - by * cx) * dz;
    if (exact.Sign != Predicates.Orient3D(a, b, c, d)) failures++;

    var p = Plane3.FromPoints(R3(), R3(), R3());
    var q = Plane3.FromPoints(R3(), R3(), R3());
    var r = Plane3.FromPoints(R3(), R3(), R3());
    if (Plane3.Intersect(p, q, r) is { } h && (Predicates.Side(p, h) != 0 || Predicates.Side(q, h) != 0 || Predicates.Side(r, h) != 0))
        failures++;
}
Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")} predicates: 20000 orient3d + plane intersections ({sw.ElapsedMilliseconds} ms)");

// Int384 multiplication throughput (interpreter vs AOT comparison point).
Int384 x = (Int384)((BigInteger.One << 190) + 12345), y = (Int384)((BigInteger.One << 101) - 777), acc = 0;
const int MulCount = 200_000;
sw.Restart();
for (int i = 0; i < MulCount; i++)
{
    acc = x * y;
    x = x + Int384.One;
}
if (acc.IsZero) failures++;
Console.WriteLine($"INFO Int384 multiply+add: {MulCount / sw.Elapsed.TotalSeconds:0} ops/s");

// Example 1, 2D.
sw.Restart();
foreach (var check in Example1.Run2D())
{
    Console.WriteLine(check);
    if (!check.Passed) failures++;
}
Console.WriteLine($"INFO example 1 (2D) took {sw.ElapsedMilliseconds} ms");

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

Vec3 R3() => new(rng.NextInt64(-M, M + 1), rng.NextInt64(-M, M + 1), rng.NextInt64(-M, M + 1));
