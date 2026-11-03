#pragma once
// ------------------------------------------------------------
// CANguru-Protokoll Version 2 - gemeinsame Bibliothek für Decoder und Bridge
//
// Quelle: 0400-Protocol/cpp/canguru_protocol.h
// Wird von alles-bauen.ps1 in Decoder und Bridge kopiert - dort NICHT ändern.
// Beschreibung des Formats: 0400-Protocol/PROTOKOLL.md
//
// Bewusst ohne Arduino-Abhängigkeit, damit dieselbe Datei auch in den
// Tests am PC (0400-Protocol/test) übersetzt wird.
// ------------------------------------------------------------

#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "protocol_constants_core.h"

// Nachrichtenkopf (8 Byte)
//  [0] CG2_MAGIC  [1] Feature  [2] Kommando  [3] Flags
//  [4] Folgenummer  [5] Decoder (ID oder Typ)  [6..7] Länge der Nutzdaten (LE)
struct Cg2Header
{
    uint8_t feature = 0;
    uint8_t command = 0;
    uint8_t flags = 0;
    uint8_t seq = 0;
    uint8_t decoder = 0;
    uint16_t length = 0;

    uint8_t addrMode() const { return (flags & CG2_ADDR_MASK) >> CG2_ADDR_SHIFT; }
    void setAddrMode(uint8_t mode)
    {
        flags = (uint8_t)((flags & ~CG2_ADDR_MASK) | ((mode << CG2_ADDR_SHIFT) & CG2_ADDR_MASK));
    }
    bool ackRequested() const { return (flags & CG2_FLAG_ACK_REQ) != 0; }
    bool isResponse() const { return (flags & CG2_FLAG_RESPONSE) != 0; }
    bool isError() const { return (flags & CG2_FLAG_ERROR) != 0; }
};

// Ergebnis von cg2Decode
enum class Cg2DecodeResult : uint8_t
{
    Ok,
    TooShort,   // weniger als CG2_HEADER_SIZE Byte
    WrongMagic, // kein CANguru-Protokoll Version 2 (z. B. altes Format)
    Truncated   // Nutzdaten kürzer als im Kopf angegeben
};

// Prüft, ob ein Puffer eine Nachricht im Format Version 2 ist (nur erstes Byte).
// Damit unterscheidet die Bridge während der Umstellung alte und neue Decoder.
inline bool cg2IsV2(const uint8_t *buf, size_t len)
{
    return len >= 1 && buf[0] == CG2_MAGIC;
}

// Zerlegt eine Nachricht. payload zeigt bei Erfolg in den Puffer (keine Kopie).
// Überzählige Bytes hinter den Nutzdaten werden ignoriert.
inline Cg2DecodeResult cg2Decode(const uint8_t *buf, size_t len, Cg2Header &h, const uint8_t *&payload)
{
    payload = nullptr;
    if (len < CG2_HEADER_SIZE)
        return Cg2DecodeResult::TooShort;
    if (buf[0] != CG2_MAGIC)
        return Cg2DecodeResult::WrongMagic;

    h.feature = buf[1];
    h.command = buf[2];
    h.flags = buf[3];
    h.seq = buf[4];
    h.decoder = buf[5];
    h.length = (uint16_t)(buf[6] | (buf[7] << 8));

    if ((size_t)h.length > len - CG2_HEADER_SIZE)
        return Cg2DecodeResult::Truncated;

    payload = buf + CG2_HEADER_SIZE;
    return Cg2DecodeResult::Ok;
}

// Baut eine Nachricht in out. h.length wird aus payloadLen gesetzt.
// Rückgabe: Gesamtlänge, 0 wenn out zu klein ist.
inline size_t cg2Encode(uint8_t *out, size_t capacity, Cg2Header h, const uint8_t *payload, size_t payloadLen)
{
    if (payloadLen > 0xFFFF || capacity < CG2_HEADER_SIZE + payloadLen)
        return 0;
    h.length = (uint16_t)payloadLen;

    out[0] = CG2_MAGIC;
    out[1] = h.feature;
    out[2] = h.command;
    out[3] = h.flags;
    out[4] = h.seq;
    out[5] = h.decoder;
    out[6] = (uint8_t)(h.length & 0xFF);
    out[7] = (uint8_t)(h.length >> 8);
    if (payloadLen > 0)
        memcpy(out + CG2_HEADER_SIZE, payload, payloadLen);
    return CG2_HEADER_SIZE + payloadLen;
}

// Kopf für die Antwort auf eine Anfrage: gleiches Feature, Kommando und Folgenummer,
// Flag "Antwort" gesetzt. decoder = eigene ID (Decoder) bzw. Absender (Bridge).
inline Cg2Header cg2ResponseTo(const Cg2Header &request, uint8_t decoder)
{
    Cg2Header r;
    r.feature = request.feature;
    r.command = request.command;
    r.flags = CG2_FLAG_RESPONSE;
    r.seq = request.seq;
    r.decoder = decoder;
    return r;
}

// ------------------------------------------------------------
// Schreiben von Nutzdaten (Little Endian), mit Bereichsprüfung.
// Nach einem Überlauf bleibt ok() false, weitere Schreibzugriffe werden ignoriert.
// ------------------------------------------------------------
class Cg2Writer
{
public:
    Cg2Writer(uint8_t *buf, size_t capacity) : _buf(buf), _cap(capacity) {}

    Cg2Writer &u8(uint8_t v)
    {
        if (fits(1))
            _buf[_pos++] = v;
        return *this;
    }
    Cg2Writer &u16(uint16_t v)
    {
        if (fits(2))
        {
            _buf[_pos++] = (uint8_t)(v & 0xFF);
            _buf[_pos++] = (uint8_t)(v >> 8);
        }
        return *this;
    }
    Cg2Writer &u32(uint32_t v)
    {
        if (fits(4))
            for (int i = 0; i < 4; i++)
                _buf[_pos++] = (uint8_t)(v >> (8 * i));
        return *this;
    }
    Cg2Writer &bytes(const uint8_t *data, size_t n)
    {
        if (fits(n))
        {
            memcpy(_buf + _pos, data, n);
            _pos += n;
        }
        return *this;
    }
    // Kurzer Text: [Länge][UTF-8-Bytes], höchstens 255 Byte
    Cg2Writer &str(const char *s)
    {
        size_t n = s ? strlen(s) : 0;
        if (n > 255)
            n = 255;
        if (fits(1 + n))
        {
            _buf[_pos++] = (uint8_t)n;
            memcpy(_buf + _pos, s, n);
            _pos += n;
        }
        return *this;
    }

    size_t size() const { return _pos; }
    bool ok() const { return _ok; }

private:
    bool fits(size_t n)
    {
        if (!_ok || _pos + n > _cap)
        {
            _ok = false;
            return false;
        }
        return true;
    }
    uint8_t *_buf;
    size_t _cap;
    size_t _pos = 0;
    bool _ok = true;
};

// ------------------------------------------------------------
// Lesen von Nutzdaten (Little Endian), mit Bereichsprüfung.
// Liest man über das Ende hinaus, liefern alle Aufrufe 0 und ok() wird false.
// ------------------------------------------------------------
class Cg2Reader
{
public:
    Cg2Reader(const uint8_t *buf, size_t len) : _buf(buf), _len(len) {}

    uint8_t u8()
    {
        return has(1) ? _buf[_pos++] : 0;
    }
    uint16_t u16()
    {
        if (!has(2))
            return 0;
        uint16_t v = (uint16_t)(_buf[_pos] | (_buf[_pos + 1] << 8));
        _pos += 2;
        return v;
    }
    uint32_t u32()
    {
        if (!has(4))
            return 0;
        uint32_t v = 0;
        for (int i = 0; i < 4; i++)
            v |= (uint32_t)_buf[_pos + i] << (8 * i);
        _pos += 4;
        return v;
    }
    // Kopiert n Byte nach out; bei zu wenig Daten wird nichts kopiert.
    bool bytes(uint8_t *out, size_t n)
    {
        if (!has(n))
            return false;
        memcpy(out, _buf + _pos, n);
        _pos += n;
        return true;
    }
    // Liest [Länge][Text] nach out (immer nullterminiert, ggf. gekürzt).
    bool str(char *out, size_t outSize)
    {
        uint8_t n = u8();
        if (!_ok || !has(n))
            return false;
        size_t copy = (n < outSize) ? n : (outSize > 0 ? outSize - 1 : 0);
        memcpy(out, _buf + _pos, copy);
        if (outSize > 0)
            out[copy] = '\0';
        _pos += n;
        return true;
    }

    size_t remaining() const { return _len - _pos; }
    bool ok() const { return _ok; }

private:
    bool has(size_t n)
    {
        if (!_ok || _pos + n > _len)
        {
            _ok = false;
            return false;
        }
        return true;
    }
    const uint8_t *_buf;
    size_t _len;
    size_t _pos = 0;
    bool _ok = true;
};

// Erkennt doppelt empfangene Anfragen (die Quittung ging verloren, der Absender
// wiederholt). Je Absender wird die letzte Folgenummer gemerkt.
class Cg2DuplicateFilter
{
public:
    // true = diese Folgenummer kam von diesem Absender gerade schon einmal
    bool isDuplicate(uint8_t sender, uint8_t seq)
    {
        bool dup = _known[sender] && _lastSeq[sender] == seq;
        _known[sender] = true;
        _lastSeq[sender] = seq;
        return dup;
    }
    void forget(uint8_t sender) { _known[sender] = false; }

private:
    uint8_t _lastSeq[256] = {};
    bool _known[256] = {};
};

// CRC-32 (IEEE 802.3, wie zlib), für Datei- und Firmware-Übertragung.
// Fortlaufend aufrufbar: crc = cg2Crc32(crc, block, n), Start mit 0.
inline uint32_t cg2Crc32(uint32_t crc, const uint8_t *data, size_t len)
{
    crc = ~crc;
    for (size_t i = 0; i < len; i++)
    {
        crc ^= data[i];
        for (int b = 0; b < 8; b++)
            crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
    }
    return ~crc;
}
