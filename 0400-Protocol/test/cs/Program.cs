// Tests der C#-Protokollbibliothek am PC (ohne Hardware).
// Aufruf: dotnet run -- <Pfad zu testvektoren.txt>; Rückgabe 0 = alle Tests bestanden.
using System.Text;
using CANguru.Protocol;

int fehler = 0, tests = 0;

void Pruefe(bool bedingung, string was)
{
    tests++;
    if (!bedingung)
    {
        fehler++;
        Console.WriteLine($"  FEHLER: {was}");
    }
}

byte[] HexZuBytes(string hex)
{
    var ziffern = new string(hex.Where(Uri.IsHexDigit).ToArray());
    return Enumerable.Range(0, ziffern.Length / 2)
        .Select(i => Convert.ToByte(ziffern.Substring(i * 2, 2), 16)).ToArray();
}

string Feld(string erwartung, string name) =>
    erwartung.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(t => t.StartsWith(name + "="))?.Substring(name.Length + 1) ?? "";

Console.WriteLine("C#-Protokolltest");

// --- Testvektoren ---
string pfad = args.Length > 0 ? args[0] : "testvektoren.txt";
int anzahl = 0;
foreach (var roh in File.ReadAllLines(pfad))
{
    string zeile = roh.Trim();
    if (zeile.Length == 0 || zeile.StartsWith('#'))
        continue;
    int a = zeile.IndexOf('|'), b = zeile.LastIndexOf('|');
    if (a < 0 || a == b)
        continue;

    string name = zeile[..a].Trim();
    byte[] bytes = HexZuBytes(zeile[(a + 1)..b]);
    string erwartung = zeile[(b + 1)..].Trim();
    anzahl++;

    var r = Cg2.Decode(bytes, out var h, out var payload);

    if (erwartung.StartsWith("FEHLER"))
    {
        string soll = erwartung[6..].Trim();
        Pruefe(soll == r.ToString(), $"{name}: erwartet {soll}, erhalten {r}");
        continue;
    }

    Pruefe(r == Cg2DecodeResult.Ok, $"{name}: nicht zerlegbar ({r})");
    if (r != Cg2DecodeResult.Ok)
        continue;

    Pruefe(Feld(erwartung, "F") == h.Feature.ToString("X2"), $"{name}: Feature");
    Pruefe(Feld(erwartung, "K") == h.Command.ToString("X2"), $"{name}: Kommando");
    Pruefe(Feld(erwartung, "FL") == h.Flags.ToString("X2"), $"{name}: Flags");
    Pruefe(Feld(erwartung, "S") == h.Seq.ToString("X2"), $"{name}: Folgenummer");
    Pruefe(Feld(erwartung, "D") == h.Decoder.ToString("X2"), $"{name}: Decoder");
    string p = Feld(erwartung, "P");
    Pruefe((p == "-" ? "" : p) == Convert.ToHexString(payload), $"{name}: Nutzdaten");

    byte[] neu = Cg2.Encode(h, payload);
    Pruefe(neu.SequenceEqual(bytes.Take(neu.Length)) && neu.Length == 8 + h.Length,
           $"{name}: Neukodierung weicht ab");
}
Pruefe(anzahl > 0, "keine Testvektoren gelesen");
Console.WriteLine($"  {anzahl} Testvektoren geprueft");

// --- Writer / Reader ---
byte[] daten = new Cg2Writer().U8(0x7F).U16(0x1234).U32(0xA1B2C3D4).Str("Weiche").ToArray();
Pruefe(daten.Length == 14, "Writer: Größe");
Pruefe(Convert.ToHexString(daten, 0, 7) == "7F3412D4C3B2A1", "Writer: Little Endian");

var rd = new Cg2Reader(daten);
Pruefe(rd.U8() == 0x7F && rd.U16() == 0x1234 && rd.U32() == 0xA1B2C3D4, "Reader: Zahlen");
Pruefe(rd.Str() == "Weiche" && rd.Ok && rd.Remaining == 0, "Reader: Text und Ende");
Pruefe(rd.U16() == 0 && !rd.Ok, "Reader: Lesen über das Ende wird erkannt");

// --- Kopf-Hilfen ---
var kopf = new Cg2Header { Flags = (byte)ProtocolConstants.CG2_FLAG_ACK_REQ };
kopf.AddrMode = ProtocolConstants.CG2_ADDR_TYPE;
Pruefe(kopf.Flags == 0x11 && kopf.AddrMode == ProtocolConstants.CG2_ADDR_TYPE && kopf.AckRequested,
       "Kopf: Adressierungsart");
kopf.Feature = (byte)ProtocolConstants.CG2_F_PARAM;
kopf.Command = (byte)ProtocolConstants.CG2_PARAM_WRITE;
kopf.Seq = 0x17;
var antwort = Cg2.ResponseTo(kopf, 5);
Pruefe(antwort.Feature == kopf.Feature && antwort.Command == kopf.Command && antwort.Seq == 0x17 &&
       antwort.Decoder == 5 && antwort.IsResponse && !antwort.AckRequested, "Kopf: Antwort auf Anfrage");

// --- CRC-32 ---
byte[] probe = Encoding.ASCII.GetBytes("123456789");
uint crc = Cg2.Crc32(0, probe, 0, 9);
Pruefe(crc == 0xCBF43926u, "CRC-32 Prüfwert");
Pruefe(Cg2.Crc32(Cg2.Crc32(0, probe, 0, 4), probe, 4, 5) == crc, "CRC-32 fortlaufend");

Console.WriteLine($"  {tests} Pruefungen, {fehler} Fehler");
return fehler == 0 ? 0 : 1;
