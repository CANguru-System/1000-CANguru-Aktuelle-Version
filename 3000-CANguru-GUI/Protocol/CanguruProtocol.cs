// ------------------------------------------------------------
// CANguru-Protokoll Version 2 - Bibliothek für die GUI
//
// Quelle: 0400-Protocol/cs/CanguruProtocol.cs
// Wird von alles-bauen.ps1 in die GUI kopiert - dort NICHT ändern.
// Gegenstück zu 0400-Protocol/cpp/canguru_protocol.h, Format: PROTOKOLL.md
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;

namespace CANguru.Protocol
{
    // Nachrichtenkopf (8 Byte)
    //  [0] CG2_MAGIC  [1] Feature  [2] Kommando  [3] Flags
    //  [4] Folgenummer  [5] Decoder (ID oder Typ)  [6..7] Länge der Nutzdaten (LE)
    public struct Cg2Header
    {
        public byte Feature;
        public byte Command;
        public byte Flags;
        public byte Seq;
        public byte Decoder;
        public ushort Length;

        public int AddrMode
        {
            get => (Flags & ProtocolConstants.CG2_ADDR_MASK) >> ProtocolConstants.CG2_ADDR_SHIFT;
            set => Flags = (byte)((Flags & ~ProtocolConstants.CG2_ADDR_MASK) |
                                  ((value << ProtocolConstants.CG2_ADDR_SHIFT) & ProtocolConstants.CG2_ADDR_MASK));
        }
        public bool AckRequested => (Flags & ProtocolConstants.CG2_FLAG_ACK_REQ) != 0;
        public bool IsResponse => (Flags & ProtocolConstants.CG2_FLAG_RESPONSE) != 0;
        public bool IsError => (Flags & ProtocolConstants.CG2_FLAG_ERROR) != 0;
    }

    public enum Cg2DecodeResult
    {
        Ok,
        TooShort,   // weniger als CG2_HEADER_SIZE Byte
        WrongMagic, // kein CANguru-Protokoll Version 2
        Truncated   // Nutzdaten kürzer als im Kopf angegeben
    }

    public static class Cg2
    {
        public static bool IsV2(byte[] data) =>
            data != null && data.Length >= 1 && data[0] == ProtocolConstants.CG2_MAGIC;

        // Zerlegt eine Nachricht. Überzählige Bytes hinter den Nutzdaten werden ignoriert.
        public static Cg2DecodeResult Decode(byte[] data, out Cg2Header h, out byte[] payload)
        {
            h = default;
            payload = Array.Empty<byte>();

            if (data == null || data.Length < ProtocolConstants.CG2_HEADER_SIZE)
                return Cg2DecodeResult.TooShort;
            if (data[0] != ProtocolConstants.CG2_MAGIC)
                return Cg2DecodeResult.WrongMagic;

            h.Feature = data[1];
            h.Command = data[2];
            h.Flags = data[3];
            h.Seq = data[4];
            h.Decoder = data[5];
            h.Length = (ushort)(data[6] | (data[7] << 8));

            if (h.Length > data.Length - ProtocolConstants.CG2_HEADER_SIZE)
                return Cg2DecodeResult.Truncated;

            payload = new byte[h.Length];
            Array.Copy(data, ProtocolConstants.CG2_HEADER_SIZE, payload, 0, h.Length);
            return Cg2DecodeResult.Ok;
        }

        // Baut eine Nachricht; h.Length wird aus payload gesetzt.
        public static byte[] Encode(Cg2Header h, byte[]? payload)
        {
            payload ??= Array.Empty<byte>();
            if (payload.Length > 0xFFFF)
                throw new ArgumentException("Nutzdaten zu lang", nameof(payload));

            var data = new byte[ProtocolConstants.CG2_HEADER_SIZE + payload.Length];
            data[0] = (byte)ProtocolConstants.CG2_MAGIC;
            data[1] = h.Feature;
            data[2] = h.Command;
            data[3] = h.Flags;
            data[4] = h.Seq;
            data[5] = h.Decoder;
            data[6] = (byte)(payload.Length & 0xFF);
            data[7] = (byte)(payload.Length >> 8);
            Array.Copy(payload, 0, data, ProtocolConstants.CG2_HEADER_SIZE, payload.Length);
            return data;
        }

        // Kopf für die Antwort auf eine Anfrage (gleiches Feature, Kommando, Folgenummer)
        public static Cg2Header ResponseTo(Cg2Header request, byte decoder) => new Cg2Header
        {
            Feature = request.Feature,
            Command = request.Command,
            Flags = (byte)ProtocolConstants.CG2_FLAG_RESPONSE,
            Seq = request.Seq,
            Decoder = decoder
        };

        // CRC-32 (IEEE 802.3, wie zlib); fortlaufend aufrufbar, Start mit 0
        public static uint Crc32(uint crc, byte[] data, int offset, int count)
        {
            crc = ~crc;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++)
                    crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
            }
            return ~crc;
        }
    }

    // Schreiben von Nutzdaten (Little Endian)
    public sealed class Cg2Writer
    {
        private readonly List<byte> _buf = new();

        public Cg2Writer U8(int v) { _buf.Add((byte)v); return this; }
        public Cg2Writer U16(int v) { _buf.Add((byte)(v & 0xFF)); _buf.Add((byte)((v >> 8) & 0xFF)); return this; }
        public Cg2Writer U32(uint v) { for (int i = 0; i < 4; i++) _buf.Add((byte)(v >> (8 * i))); return this; }
        public Cg2Writer Bytes(byte[] data) { _buf.AddRange(data); return this; }

        // Kurzer Text: [Länge][UTF-8-Bytes], höchstens 255 Byte
        public Cg2Writer Str(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? string.Empty);
            int n = Math.Min(b.Length, 255);
            _buf.Add((byte)n);
            for (int i = 0; i < n; i++) _buf.Add(b[i]);
            return this;
        }

        public byte[] ToArray() => _buf.ToArray();
    }

    // Lesen von Nutzdaten (Little Endian). Über das Ende hinaus: Ergebnis 0, Ok = false.
    public sealed class Cg2Reader
    {
        private readonly byte[] _buf;
        private int _pos;

        public Cg2Reader(byte[] buf) { _buf = buf ?? Array.Empty<byte>(); }

        public bool Ok { get; private set; } = true;
        public int Remaining => _buf.Length - _pos;

        public byte U8() => Has(1) ? _buf[_pos++] : (byte)0;

        public ushort U16()
        {
            if (!Has(2)) return 0;
            ushort v = (ushort)(_buf[_pos] | (_buf[_pos + 1] << 8));
            _pos += 2;
            return v;
        }

        public uint U32()
        {
            if (!Has(4)) return 0;
            uint v = 0;
            for (int i = 0; i < 4; i++) v |= (uint)_buf[_pos + i] << (8 * i);
            _pos += 4;
            return v;
        }

        public byte[] Bytes(int n)
        {
            if (!Has(n)) return Array.Empty<byte>();
            var b = new byte[n];
            Array.Copy(_buf, _pos, b, 0, n);
            _pos += n;
            return b;
        }

        public string Str()
        {
            int n = U8();
            if (!Ok || !Has(n)) return string.Empty;
            string s = Encoding.UTF8.GetString(_buf, _pos, n);
            _pos += n;
            return s;
        }

        private bool Has(int n)
        {
            if (!Ok || _pos + n > _buf.Length) { Ok = false; return false; }
            return true;
        }
    }
}
