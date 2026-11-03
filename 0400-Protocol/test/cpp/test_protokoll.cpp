// Tests der C++-Protokollbibliothek am PC (ohne Hardware).
// Aufruf: test_protokoll <Pfad zu testvektoren.txt>
// Übersetzt von alles-bauen.ps1 mit MSVC; Rückgabe 0 = alle Tests bestanden.

#include <cctype>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include "canguru_protocol.h"

static int g_fehler = 0;
static int g_tests = 0;

static void pruefe(bool bedingung, const std::string &was)
{
    g_tests++;
    if (!bedingung)
    {
        g_fehler++;
        std::printf("  FEHLER: %s\n", was.c_str());
    }
}

static std::string trim(const std::string &s)
{
    size_t a = s.find_first_not_of(" \t\r\n");
    if (a == std::string::npos)
        return "";
    size_t b = s.find_last_not_of(" \t\r\n");
    return s.substr(a, b - a + 1);
}

static std::vector<uint8_t> hexZuBytes(const std::string &hex)
{
    std::vector<uint8_t> out;
    std::string ziffern;
    for (char c : hex)
        if (std::isxdigit((unsigned char)c))
            ziffern += c;
    for (size_t i = 0; i + 1 < ziffern.size(); i += 2)
        out.push_back((uint8_t)std::stoul(ziffern.substr(i, 2), nullptr, 16));
    return out;
}

static std::string bytesZuHex(const uint8_t *p, size_t n)
{
    std::string s;
    char buf[3];
    for (size_t i = 0; i < n; i++)
    {
        std::snprintf(buf, sizeof(buf), "%02X", p[i]);
        s += buf;
    }
    return s;
}

static const char *ergebnisName(Cg2DecodeResult r)
{
    switch (r)
    {
    case Cg2DecodeResult::Ok: return "OK";
    case Cg2DecodeResult::TooShort: return "TooShort";
    case Cg2DecodeResult::WrongMagic: return "WrongMagic";
    case Cg2DecodeResult::Truncated: return "Truncated";
    }
    return "?";
}

// Liest "F=04" usw. aus der Erwartung
static std::string feld(const std::string &erwartung, const std::string &name)
{
    std::istringstream is(erwartung);
    std::string teil;
    while (is >> teil)
        if (teil.rfind(name + "=", 0) == 0)
            return teil.substr(name.size() + 1);
    return "";
}

static void testeVektoren(const char *pfad)
{
    std::ifstream f(pfad);
    if (!f)
    {
        pruefe(false, std::string("Testvektoren nicht gefunden: ") + pfad);
        return;
    }
    std::string zeile;
    int anzahl = 0;
    while (std::getline(f, zeile))
    {
        if (trim(zeile).empty() || trim(zeile)[0] == '#')
            continue;
        size_t a = zeile.find('|'), b = zeile.rfind('|');
        if (a == std::string::npos || a == b)
            continue;
        std::string name = trim(zeile.substr(0, a));
        std::vector<uint8_t> bytes = hexZuBytes(zeile.substr(a + 1, b - a - 1));
        std::string erwartung = trim(zeile.substr(b + 1));
        anzahl++;

        Cg2Header h;
        const uint8_t *payload = nullptr;
        Cg2DecodeResult r = cg2Decode(bytes.data(), bytes.size(), h, payload);

        if (erwartung.rfind("FEHLER", 0) == 0)
        {
            std::string soll = trim(erwartung.substr(6));
            pruefe(soll == ergebnisName(r), name + ": erwartet " + soll + ", erhalten " + ergebnisName(r));
            continue;
        }

        pruefe(r == Cg2DecodeResult::Ok, name + ": nicht zerlegbar (" + ergebnisName(r) + ")");
        if (r != Cg2DecodeResult::Ok)
            continue;

        auto hex2 = [](uint8_t v) { char s[3]; std::snprintf(s, sizeof(s), "%02X", v); return std::string(s); };
        pruefe(feld(erwartung, "F") == hex2(h.feature), name + ": Feature");
        pruefe(feld(erwartung, "K") == hex2(h.command), name + ": Kommando");
        pruefe(feld(erwartung, "FL") == hex2(h.flags), name + ": Flags");
        pruefe(feld(erwartung, "S") == hex2(h.seq), name + ": Folgenummer");
        pruefe(feld(erwartung, "D") == hex2(h.decoder), name + ": Decoder");
        std::string p = feld(erwartung, "P");
        pruefe((p == "-" ? std::string() : p) == bytesZuHex(payload, h.length), name + ": Nutzdaten");

        // neu kodieren und vergleichen
        uint8_t neu[600];
        size_t n = cg2Encode(neu, sizeof(neu), h, payload, h.length);
        pruefe(n == CG2_HEADER_SIZE + h.length &&
                   std::memcmp(neu, bytes.data(), n) == 0,
               name + ": Neukodierung weicht ab");
    }
    pruefe(anzahl > 0, "keine Testvektoren gelesen");
    std::printf("  %d Testvektoren geprueft\n", anzahl);
}

static void testeWriterReader()
{
    uint8_t buf[32];
    Cg2Writer w(buf, sizeof(buf));
    w.u8(0x7F).u16(0x1234).u32(0xA1B2C3D4).str("Weiche");
    pruefe(w.ok() && w.size() == 1 + 2 + 4 + 7, "Writer: Größe");
    pruefe(bytesZuHex(buf, 7) == "7F3412D4C3B2A1", "Writer: Little Endian");

    Cg2Reader r(buf, w.size());
    pruefe(r.u8() == 0x7F && r.u16() == 0x1234 && r.u32() == 0xA1B2C3D4, "Reader: Zahlen");
    char text[16];
    pruefe(r.str(text, sizeof(text)) && std::strcmp(text, "Weiche") == 0, "Reader: Text");
    pruefe(r.ok() && r.remaining() == 0, "Reader: Ende");
    pruefe(r.u16() == 0 && !r.ok(), "Reader: Lesen über das Ende wird erkannt");

    uint8_t klein[3];
    Cg2Writer w2(klein, sizeof(klein));
    w2.u16(1).u16(2);
    pruefe(!w2.ok() && w2.size() == 2, "Writer: Überlauf wird erkannt");
}

static void testeKopfHilfen()
{
    Cg2Header h;
    h.flags = CG2_FLAG_ACK_REQ;
    h.setAddrMode(CG2_ADDR_TYPE);
    pruefe(h.flags == 0x11 && h.addrMode() == CG2_ADDR_TYPE && h.ackRequested(), "Kopf: Adressierungsart");

    h.feature = CG2_F_PARAM;
    h.command = CG2_PARAM_WRITE;
    h.seq = 0x17;
    Cg2Header a = cg2ResponseTo(h, 5);
    pruefe(a.feature == CG2_F_PARAM && a.command == CG2_PARAM_WRITE && a.seq == 0x17 &&
               a.decoder == 5 && a.isResponse() && !a.ackRequested(),
           "Kopf: Antwort auf Anfrage");

    uint8_t out[4];
    pruefe(cg2Encode(out, sizeof(out), h, nullptr, 0) == 0, "Kopf: zu kleiner Puffer wird erkannt");
}

static void testeDuplikate()
{
    Cg2DuplicateFilter d;
    pruefe(!d.isDuplicate(3, 10), "Duplikat: erste Nachricht");
    pruefe(d.isDuplicate(3, 10), "Duplikat: Wiederholung erkannt");
    pruefe(!d.isDuplicate(3, 11), "Duplikat: neue Folgenummer");
    pruefe(!d.isDuplicate(4, 11), "Duplikat: anderer Absender");
}

static void testeCrc()
{
    const char *probe = "123456789";
    uint32_t c = cg2Crc32(0, (const uint8_t *)probe, 9);
    pruefe(c == 0xCBF43926u, "CRC-32 Prüfwert");
    uint32_t teil = cg2Crc32(0, (const uint8_t *)probe, 4);
    pruefe(cg2Crc32(teil, (const uint8_t *)probe + 4, 5) == c, "CRC-32 fortlaufend");
}

int main(int argc, char **argv)
{
    std::printf("C++-Protokolltest\n");
    testeVektoren(argc > 1 ? argv[1] : "testvektoren.txt");
    testeWriterReader();
    testeKopfHilfen();
    testeDuplikate();
    testeCrc();
    std::printf("  %d Pruefungen, %d Fehler\n", g_tests, g_fehler);
    return g_fehler == 0 ? 0 : 1;
}
