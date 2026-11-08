using CANguru.Models;
using CANguru.Protocol;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CANguru.Networking
{
    // Answer of the bridge to DISCOVER
    public sealed record BridgeInfo(IPAddress Address, int FwMajor, int FwMinor, int ProtoMinor, bool Gleisbox);

    // Session with the bridge: receives on GUI_PORT, searches the bridge (DISCOVER once per
    // second until it answers), decodes protocol-v2 messages and raises one event per kind.
    // Answers to own requests are handed to BridgeLink.
    //
    // All events are raised on the receive thread; handlers that touch the UI must invoke.
    public sealed class BridgeSession
    {
        public event Action<string>? Log;
        public event Action<BridgeInfo>? BridgeFound;
        public event Action<bool>? TrackPowerChanged;
        // [seq] and the CAN frame with source byte (14 bytes)
        public event Action<uint, byte[]>? CanFrameReceived;
        // id, type, mac, firmware major, firmware minor
        public event Action<byte, DecoderType, byte[], int, int>? DecoderAdded;
        // id, status, measured values
        public event Action<byte, byte, int[]>? DecoderAlive;
        // ready decoders per device type (DEVTYPE_*)
        public event Action<IReadOnlyDictionary<byte, int>>? AliveSummary;
        // id, event (CG2_EV_*), event data
        public event Action<byte, byte, byte[]>? DecoderEvent;
        // decoder id, percent of this decoder, overall percent
        public event Action<int, int, int>? OtaProgress;
        // decoder id, result (0 ok, 1 failed, 2 no answer), reason (CG2_OTA_FAIL_*)
        public event Action<byte, byte, int>? OtaResult;

        private UdpClient? receiver;
        private System.Threading.Timer? discoverTimer;
        private volatile bool bridgeFound;
        private volatile bool stopped;

        public bool IsBridgeFound => bridgeFound;

        public void Start()
        {
            stopped = false;
            bridgeFound = false;
            receiver = new UdpClient(ProtocolConstants.GUI_PORT);
            receiver.BeginReceive(OnUdpData, null);

            SendDiscover();
            discoverTimer = new System.Threading.Timer(_ => SendDiscover(), null, 1000, 1000);
        }

        // Ignore everything from now on (program is closing)
        public void Stop()
        {
            stopped = true;
            discoverTimer?.Dispose();
            discoverTimer = null;
        }

        // SESSION DISCOVER as broadcast: [protoMinor]
        private void SendDiscover()
        {
            if (bridgeFound || stopped)
            {
                discoverTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            BridgeLink.Instance.Broadcast((byte)ProtocolConstants.CG2_F_SESSION,
                                          (byte)ProtocolConstants.CG2_SESSION_DISCOVER,
                                          new byte[] { (byte)ProtocolConstants.CG2_PROTO_MINOR });
        }

        private void OnUdpData(IAsyncResult ar)
        {
            if (stopped || receiver == null)
                return;

            var remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] data;
            try
            {
                data = receiver.EndReceive(ar, ref remote!);
            }
            catch (ObjectDisposedException)
            {
                return; // socket closed while shutting down
            }
            catch (SocketException)
            {
                return;
            }

            if (stopped)
                return;

            // A broken packet must not end the GUI: an exception on this thread
            // would terminate the process
            try
            {
                ProcessPacket(data, remote);
            }
            catch (Exception ex)
            {
                if (!stopped)
                    Log?.Invoke($"Fehlerhaftes Paket von der Bridge ignoriert ({ex.GetType().Name}): {BitConverter.ToString(data)}");
            }

            try
            {
                receiver.BeginReceive(OnUdpData, null);
            }
            catch
            {
                // may fail while closing
            }
        }

        private void ProcessPacket(byte[] data, IPEndPoint remote)
        {
            // The old format is no longer evaluated; the bridge translates old decoders
            if (!Cg2.IsV2(data))
            {
                Debug.WriteLine($"Nachricht im alten Format von {remote} ignoriert ({data.Length} Bytes).");
                return;
            }

            if (Cg2.Decode(data, out var h, out var payload) != Cg2DecodeResult.Ok)
            {
                Log?.Invoke($"Ungültige Nachricht von der Bridge ignoriert: {BitConverter.ToString(data)}");
                return;
            }

            // answers to own requests go to the waiting caller in BridgeLink
            if (BridgeLink.Instance.HandleResponse(h, payload))
                return;

            switch (h.Feature)
            {
                case ProtocolConstants.CG2_F_SESSION:
                    if (h.Command == ProtocolConstants.CG2_SESSION_DISCOVER_REPLY)
                        HandleDiscoverReply(remote.Address, payload);
                    else if (h.Command == ProtocolConstants.CG2_SESSION_TRACK_POWER && payload.Length >= 1)
                        TrackPowerChanged?.Invoke(payload[0] != 0);
                    return;

                case ProtocolConstants.CG2_F_CAN:
                    // [seq u32][frame 13][source]
                    if (h.Command == ProtocolConstants.CG2_CAN_TO_GUI &&
                        payload.Length >= 4 + ProtocolConstants.CAN_FRAME_SIZE + 1)
                        CanFrameReceived?.Invoke(BitConverter.ToUInt32(payload, 0), payload[4..]);
                    return;

                case ProtocolConstants.CG2_F_OTA:
                    // PROGRESS: [id][percent][overall]  RESULT: [id][result][reason]
                    if (h.Command == ProtocolConstants.CG2_OTA_PROGRESS && payload.Length >= 3)
                        OtaProgress?.Invoke(payload[0], payload[1], Math.Min((int)payload[2], 100));
                    else if (h.Command == ProtocolConstants.CG2_OTA_RESULT && payload.Length >= 2)
                        OtaResult?.Invoke(payload[0], payload[1],
                                          payload.Length >= 3 ? payload[2] : ProtocolConstants.CG2_OTA_FAIL_NONE);
                    return;

                case ProtocolConstants.CG2_F_DECODER:
                    // ADDED: [id][type][mac 6][fwMajor][fwMinor][protoMinor][reserved]
                    if (h.Command == ProtocolConstants.CG2_DECODER_ADDED && payload.Length >= 10)
                        DecoderAdded?.Invoke(payload[0], (DecoderType)payload[1], payload[2..8], payload[8], payload[9]);
                    return;

                case ProtocolConstants.CG2_F_ALIVE:
                    if (h.Command == ProtocolConstants.CG2_ALIVE_ALIVE)
                        HandleAlive(h.Decoder, payload);
                    else if (h.Command == ProtocolConstants.CG2_ALIVE_SUMMARY)
                        HandleSummary(payload);
                    return;

                case ProtocolConstants.CG2_F_EVENT:
                    // [event][data]
                    if (h.Command == ProtocolConstants.CG2_EVENT_EVENT && payload.Length >= 1)
                        DecoderEvent?.Invoke(h.Decoder, payload[0], payload[1..]);
                    return;
            }
        }

        // DISCOVER reply: [fwMajor][fwMinor][protoMinor][trackbox 0/1]
        private void HandleDiscoverReply(IPAddress bridgeIp, byte[] payload)
        {
            if (bridgeFound)
                return;
            bridgeFound = true;
            discoverTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            var r = new Cg2Reader(payload);
            byte fwMajor = r.U8(), fwMinor = r.U8(), protoMinor = r.U8(), gleisbox = r.U8();

            // from now on send directly to the bridge instead of broadcasting
            ProtocolConfig.BridgeIP = bridgeIp;
            ProtocolConfig.ipString = bridgeIp.ToString();
            UdpClientSingleton.Instance.SetBridge(bridgeIp);

            BridgeFound?.Invoke(new BridgeInfo(bridgeIp, fwMajor, fwMinor, protoMinor, gleisbox == 1));
        }

        // ALIVE: [status][count][value u16] x count
        private void HandleAlive(byte decoderId, byte[] payload)
        {
            var r = new Cg2Reader(payload);
            byte status = r.U8();
            int count = r.U8();
            var values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = r.U16();
            if (r.Ok)
                DecoderAlive?.Invoke(decoderId, status, values);
        }

        // SUMMARY: [count][type][number] x count
        private void HandleSummary(byte[] payload)
        {
            if (payload.Length < 1)
                return;
            var counts = new Dictionary<byte, int>();
            int n = payload[0];
            for (int i = 0; i < n && 2 + 2 * i < payload.Length; i++)
                counts[payload[1 + 2 * i]] = payload[2 + 2 * i];
            AliveSummary?.Invoke(counts);
        }
    }
}
