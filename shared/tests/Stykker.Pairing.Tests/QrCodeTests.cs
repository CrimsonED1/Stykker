using Stykker.Pairing;

namespace Stykker.Pairing.Tests;

// Der QR-Encoder aus StykkerLLM (Lizenz hier Apache-2.0). Die erwarteten Werte sind von Hand gerechnet:
// 41 Bytes + 4 Bit Modus + 8 Bit Länge = 340 Bit. Version 2 hat auf Stufe M 224 Bit, Version 3 352 Bit,
// also Version 3 mit 29 Modulen je Seite. Version 10 auf Stufe M fasst 216 Datenwörter = 1728 Bit,
// abzüglich 4 Bit Modus und 16 Bit Länge sind das 213 Bytes.
public sealed class QrCodeTests
{
    private const string PairUrl = "http://192.0.2.10:8078/pair?code=ABCD2345";

    [Fact]
    public void FixedUrl_UsesVersion3_With29Modules()
    {
        var qr = QrCode.Encode(PairUrl, QrCode.Ecc.M);

        Assert.Equal(QrCode.Ecc.M, qr.Level);
        Assert.Equal(3, qr.Version);
        Assert.Equal(29, qr.Size);
        Assert.True(qr[0, 0], "Ecke oben links des Finder-Musters ist dunkel");
    }

    [Fact]
    public void Svg_HasOneRectPerDarkModule_AndTheExpectedViewBox()
    {
        var qr = QrCode.Encode(PairUrl);
        const int module = 4, margin = 4;
        int side = qr.Size + margin * 2;

        var svg = qr.ToSvg(module, margin);

        Assert.StartsWith("<svg ", svg);
        Assert.Contains($"width=\"{side * module}\" height=\"{side * module}\"", svg);
        Assert.Contains($"viewBox=\"0 0 {side} {side}\"", svg);
        // Ein Rechteck je dunklem Modul, dazu der helle Hintergrund
        Assert.Equal(qr.DarkCount + 1, svg.Split("<rect").Length - 1);
    }

    [Fact]
    public void LongestText_ForVersion10_Fits_AndOneByteMoreIsRefused()
    {
        var qr = QrCode.Encode(new string('x', 213), QrCode.Ecc.M);
        Assert.Equal(10, qr.Version);
        Assert.Equal(57, qr.Size);

        var error = Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 214), QrCode.Ecc.M));
        Assert.Contains("too long", error.Message);
    }

    [Fact]
    public void EmptyText_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(""));
    }
}
