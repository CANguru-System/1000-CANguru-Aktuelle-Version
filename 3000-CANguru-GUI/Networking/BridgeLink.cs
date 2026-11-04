using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CANguru.Protocol;

namespace CANguru.Networking
{
    // Antwort auf eine Anfrage im Protokoll Version 2
    public sealed class Cg2Reply
    {
        public Cg2Header Header { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();

        public bool IsError => Header.IsError;
        public byte ErrorCode => IsError && Payload.Length > 0 ? Payload[0] : (byte)0;
    }

    // Verbindung GUI -> Bridge im CANguru-Protokoll Version 2 (0400-Protocol/PROTOKOLL.md):
    // Folgenummern, Senden, Anfragen mit Quittung und Wiederholung.
    public sealed class BridgeLink
    {
        public static BridgeLink Instance { get; } = new();

        // Startwert zufällig, damit eine neu gestartete GUI nicht zufällig die
        // Folgenummer der letzten Anfrage wiederholt
        private int _seq = Random.Shared.Next(256);

        private readonly ConcurrentDictionary<int, TaskCompletionSource<Cg2Reply>> _pending = new();

        private BridgeLink() { }

        private byte NextSeq() => (byte)(Interlocked.Increment(ref _seq) & 0xFF);

        private static int Key(byte feature, byte command, byte seq) => (feature << 16) | (command << 8) | seq;

        private static Cg2Header MakeHeader(byte feature, byte command, byte decoder, byte flags, byte seq) => new()
        {
            Feature = feature,
            Command = command,
            Flags = flags,
            Seq = seq,
            Decoder = decoder
        };

        // Nachricht ohne Quittung (z. B. CAN-Frames)
        public void Send(byte feature, byte command, byte decoder, byte[]? payload)
        {
            var h = MakeHeader(feature, command, decoder, 0, NextSeq());
            UdpClientSingleton.Instance.Send(Cg2.Encode(h, payload));
        }

        // Nachricht an alle Bridges im Netz (nur zum Suchen der Bridge)
        public void Broadcast(byte feature, byte command, byte[]? payload)
        {
            var h = MakeHeader(feature, command, (byte)ProtocolConstants.CG2_ID_BRIDGE, 0, NextSeq());
            UdpClientSingleton.Instance.SendBroadcast(Cg2.Encode(h, payload));
        }

        // Anfrage mit Quittung. Ohne Antwort wird bis zu CG2_RETRY_COUNT Mal wiederholt.
        // Rückgabe: Antwort (ggf. mit Fehlercode) oder null, wenn keine Antwort kam.
        public async Task<Cg2Reply?> RequestAsync(byte feature, byte command, byte decoder, byte[]? payload,
                                                 int timeoutMs = 500, CancellationToken ct = default)
        {
            byte seq = NextSeq();
            var h = MakeHeader(feature, command, decoder, (byte)ProtocolConstants.CG2_FLAG_ACK_REQ, seq);
            byte[] frame = Cg2.Encode(h, payload);

            int key = Key(feature, command, seq);
            var tcs = new TaskCompletionSource<Cg2Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[key] = tcs;
            try
            {
                for (int attempt = 0; attempt <= ProtocolConstants.CG2_RETRY_COUNT; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    UdpClientSingleton.Instance.Send(frame);

                    // ConfigureAwait(false): nicht an den UI-Thread binden, damit Request() aus
                    // dem UI-Thread blockierend warten kann, ohne sich selbst zu blockieren
                    var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
                    if (done == tcs.Task)
                        return tcs.Task.Result;
                }
                return null;
            }
            finally
            {
                _pending.TryRemove(key, out _);
            }
        }

        // Blockierende Variante für Stellen, die das Ergebnis sofort brauchen (z. B. beim
        // Öffnen eines Einstellungsfensters). Wartet höchstens (CG2_RETRY_COUNT + 1) * timeoutMs.
        public Cg2Reply? Request(byte feature, byte command, byte decoder, byte[]? payload, int timeoutMs = 500) =>
            RequestAsync(feature, command, decoder, payload, timeoutMs).GetAwaiter().GetResult();

        // Aus dem Empfang aufrufen. true = die Nachricht war eine erwartete Antwort.
        public bool HandleResponse(Cg2Header h, byte[] payload)
        {
            if (!h.IsResponse)
                return false;
            if (_pending.TryRemove(Key(h.Feature, h.Command, h.Seq), out var tcs))
            {
                tcs.TrySetResult(new Cg2Reply { Header = h, Payload = payload });
                return true;
            }
            return true; // verspätete Antwort auf eine schon beendete Anfrage: ignorieren
        }

        // Klartext zu einem Fehlercode für Meldungen in der GUI
        public static string ErrorText(byte code) => code switch
        {
            (byte)ProtocolConstants.CG2_ERR_UNKNOWN_FEATURE => "unbekanntes Feature",
            (byte)ProtocolConstants.CG2_ERR_UNKNOWN_COMMAND => "unbekanntes Kommando",
            (byte)ProtocolConstants.CG2_ERR_BAD_LENGTH => "Nachricht zu kurz",
            (byte)ProtocolConstants.CG2_ERR_BAD_VALUE => "ungültiger Wert",
            (byte)ProtocolConstants.CG2_ERR_BUSY => "gerade nicht möglich",
            (byte)ProtocolConstants.CG2_ERR_NOT_DELIVERED => "Decoder antwortet nicht",
            (byte)ProtocolConstants.CG2_ERR_UNKNOWN_DECODER => "Decoder unbekannt",
            _ => $"Fehler {code}"
        };
    }
}
