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

// --- Beschreibung eines Auswahl-Parameters (PROTOKOLL.md 5.4), dieselben Bytes wie im C++-Test ---
const string beschreibungHex =
    "000102020000010000000100000C4C6175667269636874756E670209766F7277C3A47274730B72C3BC636B77C3A4727473";
byte[] beschreibung = new Cg2Writer()
    .U8((byte)ProtocolConstants.CG2_KIND_PARAM).U8(1).U8(2).U8((byte)ProtocolConstants.CG2_TYPE_CHOICE)
    .U16(0).U16(1).U16(0).U16(1).Str("").Str("Laufrichtung").U8(2).Str("vorwärts").Str("rückwärts")
    .ToArray();
Pruefe(Convert.ToHexString(beschreibung) == beschreibungHex, "Beschreibung: Kodierung");
var rb = new Cg2Reader(beschreibung);
bool bOk = rb.U8() == ProtocolConstants.CG2_KIND_PARAM && rb.U8() == 1 && rb.U8() == 2 &&
           rb.U8() == ProtocolConstants.CG2_TYPE_CHOICE && rb.U16() == 0 && rb.U16() == 1 && rb.U16() == 0 &&
           rb.U16() == 1 && rb.Str() == "" && rb.Str() == "Laufrichtung" && rb.U8() == 2 &&
           rb.Str() == "vorwärts" && rb.Str() == "rückwärts" && rb.Ok && rb.Remaining == 0;
Pruefe(bOk, "Beschreibung: Zerlegung");

var dAuswahl = Cg2Description.Parse(beschreibung, out int nAuswahl);
Pruefe(dAuswahl != null && nAuswahl == 2 && dAuswahl.Name == "Laufrichtung" && dAuswahl.Choices.Length == 2 &&
       dAuswahl.FormatValue(1) == "rückwärts" && dAuswahl.FormatValue(7) == "7" && dAuswahl.FormatValue(0xFFFF) == "–",
       "Beschreibung: Parse und Klartext einer Auswahl");
Pruefe(dAuswahl != null && dAuswahl.Group == "" && dAuswahl.Label == "Laufrichtung", "Gruppenregel: ohne Gruppe");
Pruefe(Cg2Description.Parse(beschreibung[..^3], out _) == null, "Beschreibung: abgeschnittene Nutzdaten erkannt");

// --- Messwert als Bitmaske (Protokoll 2.1), dieselben Bytes wie im C++-Test ---
const string bitsHex = "020001040000000000000000000642656C65677403024B31024B32024B33";
byte[] bits = new Cg2Writer()
    .U8((byte)ProtocolConstants.CG2_KIND_VALUE).U8(0).U8(1).U8((byte)ProtocolConstants.CG2_TYPE_BITS)
    .U16(0).U16(0).U16(0).U16(0).Str("").Str("Belegt").U8(3).Str("K1").Str("K2").Str("K3")
    .ToArray();
Pruefe(Convert.ToHexString(bits) == bitsHex, "Bitmaske: Kodierung");
var dBits = Cg2Description.Parse(bits, out int nBits);
Pruefe(dBits != null && nBits == 1 && dBits.Kind == ProtocolConstants.CG2_KIND_VALUE &&
       dBits.Type == ProtocolConstants.CG2_TYPE_BITS && dBits.Choices.Length == 3, "Bitmaske: Zerlegung");
Pruefe(dBits != null && dBits.FormatValue(0) == "keine" && dBits.FormatValue(0b101) == "K1, K3" &&
       dBits.FormatValue(0b1010) == "K2, 4", "Bitmaske: Klartext (Bit ohne Namen = Nummer)");

// --- Gruppenregel ---
var gruppe = new Cg2Description { Name = "Signal 3: Adresse" };
Pruefe(gruppe.Group == "Signal 3" && gruppe.Label == "Adresse", "Gruppenregel: Gruppe und Name");
var ohneLeer = new Cg2Description { Name = "Zeit:10" };
Pruefe(ohneLeer.Group == "" && ohneLeer.Label == "Zeit:10", "Gruppenregel: Doppelpunkt ohne Leerzeichen zählt nicht");

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
