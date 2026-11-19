using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using CANguru.Models;
using Timer = System.Threading.Timer;

namespace CANguru.Controllers
{
    public class CANguruLokRegistry
    {
        private readonly Dictionary<uint, CANguruLok> loks = new();

        public IEnumerable<CANguruLok> AllLoks => loks.Values;

        public bool Contains(uint uid) => loks.ContainsKey(uid);

        public CANguruLok GetOrCreate(uint uid)
        {
            // Prüfen, ob Lok bereits existiert
            if (loks.TryGetValue(uid, out var lok))
                return lok;

            // Neue Lok anlegen
            lok = new CANguruLok
            {
                UID = uid,
            };

            loks[uid] = lok;
            lok.IsManual = false;
            lok.DecoderType = "";
            lok.MfxUID = new byte[4];   // WICHTIG!
            return lok;
        }

        public void Forget(uint uid) => loks.Remove(uid);
    }

    // mfx registration, modelled on the Mobile Station 2 (sniffer recordings 19.11.2026,
    // 5000-Texte/Buch/Quellen/Sniffer/Auswertung.md):
    //
    //  - The track box searches for new mfx locomotives by itself as soon as one is put on
    //    the track (mfx discovery answers). The last answer without quality byte (DLC 5)
    //    carries the UID that was found.
    //  - Without a Mobile Station the GUI then registers the locomotive: a known UID gets its
    //    previous address (SID), a new one the next free address; Bind, Verify, read the name.
    //    The functions are read afterwards by the main window (LokCompleted).
    //  - With a Mobile Station on the bus the MS2 binds by itself (first a temporary SID 1,
    //    then the final one). The GUI only listens: it takes the final SID from the MS2's
    //    Bind commands and reads the name once the MS2 has finished.
    //  - The registration counter is not shown any more: it stays the same at every start
    //    (otherwise all locomotives register again) and is increased only when an mfx
    //    locomotive is removed.
    public class MfxParser
    {
        // time without further Bind from the MS2 before its last Bind counts as final
        private const int Ms2SettleMs = 3000;
        // a registration that does not finish in this time is given up
        private const int RegistrationTimeoutMs = 20000;
        // after the track box answered: wait for a possible MS2 before preparing the track box
        private const int Ms2DetectMs = 3000;

        private readonly object sync = new();
        private readonly CANguruLokRegistry registry;
        private readonly CANFrames canFrames;

        private bool gleisboxFound;
        private bool trackPrepared;

        // running registration (GUI or listening to the MS2)
        private bool registering;
        private bool readingName;
        private byte[] currentUid = new byte[4];
        private byte currentSid;
        private Timer? timeoutTimer;

        // listening to the MS2: last Bind per UID
        private readonly Dictionary<uint, byte> ms2Binds = new();
        private Timer? ms2SettleTimer;
        private uint ms2SettleUid;

        public event Action<string>? LogMessage;
        public event Action<byte[]>? SendFrameRequested;
        public event Action<CANguruLok>? LokCompleted;
        // a known mfx locomotive registered again (same address), name not read again
        public event Action<uint, byte>? LokReturned;
        // the registration counter changed and must be saved
        public event Action<byte>? CounterChanged;

        // previous address (SID) of a known mfx locomotive, otherwise null
        public Func<uint, byte?>? FindKnownSid;
        // next free mfx address
        public Func<byte>? NextFreeSid;

        public bool Ms2Present { get; private set; }

        public MfxParser(CANguruLokRegistry registry, CANFrames canFrames)
        {
            this.registry = registry;
            this.canFrames = canFrames;
        }

        // ------------------------------------------------------------
        // CAN frames from the bus
        // ------------------------------------------------------------

        public void ProcessCanFrame(byte[] data)
        {
            if (data.Length < 13)
                return;
            switch (data[0x01])
            {
                case CANFrames.PING_CMD_R:
                    OnPingAnswer(data);
                    break;

                case CANFrames.LokDiscovery_CMD_R:
                    // the track box found a locomotive: last answer without quality byte
                    if (data[0x04] == 0x05)
                        OnLokFound(UidOf(data));
                    break;

                case CANFrames.MFXBind_CMD:
                    // Bind from another device (the MS2)
                    if (data[0x04] == 0x06)
                        OnForeignBind(UidOf(data), data[0x0A]);
                    break;

                case CANFrames.MFXVerify_CMD_R:
                    OnVerifyAnswer(data);
                    break;

                case CANFrames.ReadConfig_CMD_R:
                    if (data[0x0A] == 0x03)
                        HandleMfxNameFrame(data);
                    break;
            }
        }

        private static byte[] UidOf(byte[] data)
        {
            var uid = new byte[4];
            Buffer.BlockCopy(data, 5, uid, 0, 4);
            return uid;
        }

        private static uint Key(byte[] uid) => BitConverter.ToUInt32(uid, 0);

        private static string Hex(byte[] uid) => string.Concat(uid.Select(b => b.ToString("X2")));

        // Ping answers tell who is on the bus: track box 0x0010, Mobile Station 0x0030 ... 0x0033
        private void OnPingAnswer(byte[] data)
        {
            if (data[0x04] < 8)
                return;
            int device = (data[0x0B] << 8) | data[0x0C];
            if (device >= ProtocolConstants.DEVTYPE_MS2 && device <= ProtocolConstants.DEVTYPE_MS2 + 3 && !Ms2Present)
            {
                Ms2Present = true;
                LogMessage?.Invoke("Mobile Station erkannt: Sie meldet mfx-Loks an, die GUI hört mit.");
            }
            if (device == ProtocolConstants.DEVTYPE_GB && !gleisboxFound)
            {
                gleisboxFound = true;
                LogMessage?.Invoke("Gleisbox gefunden.");
                // give a Mobile Station the chance to answer the ping, then prepare the track box
                new Timer(_ => PrepareTrackBox(), null, Ms2DetectMs, Timeout.Infinite);
            }
        }

        // Without a Mobile Station the GUI is the central: enable all track protocols and send
        // the stored registration counter (unchanged, so the locomotives do not register again).
        private void PrepareTrackBox()
        {
            lock (sync)
            {
                if (trackPrepared || Ms2Present)
                    return;
                trackPrepared = true;
            }
            SendFrameRequested?.Invoke(canFrames.GLEISBOX_ALL_PROTO_ENABLE);
            SendCounter();
            LogMessage?.Invoke($"mfx-Anmeldung bereit (Neuanmeldezähler {canFrames.newSubscriptionCounter}).");
        }

        private void SendCounter()
        {
            canFrames.Neuanmeldezaehler[0x0A] = 0;
            canFrames.Neuanmeldezaehler[0x0B] = canFrames.newSubscriptionCounter;
            SendFrameRequested?.Invoke(canFrames.Neuanmeldezaehler);
        }

        // ------------------------------------------------------------
        // Registration by the GUI (no Mobile Station)
        // ------------------------------------------------------------

        private void OnLokFound(byte[] uid)
        {
            if (Ms2Present)
                return; // the MS2 binds; the GUI follows its Bind commands
            byte sid;
            bool known;
            lock (sync)
            {
                if (registering)
                    return; // one at a time; the track box reports the other one again
                byte? knownSid = FindKnownSid?.Invoke(Key(uid));
                known = knownSid.HasValue;
                sid = knownSid ?? NextFreeSid?.Invoke() ?? canFrames.nextLokAddress;
                StartRegistration(uid, sid, readName: !known);
            }
            LogMessage?.Invoke(known
                ? $"Bekannte mfx-Lok {Hex(uid)} meldet sich an, behält Adresse {sid}."
                : $"Neue mfx-Lok {Hex(uid)} gefunden, bekommt Adresse {sid}.");
            SendBind(uid, sid);
            SendVerify(uid, sid);
        }

        private void StartRegistration(byte[] uid, byte sid, bool readName)
        {
            registering = true;
            readingName = readName;
            currentUid = uid;
            currentSid = sid;
            Buffer.BlockCopy(uid, 0, canFrames.lastmfxUID, 0, 4);
            var lok = registry.GetOrCreate(Key(uid));
            lok.NameFragments.Clear();
            lok.NameFramesReceived = 0;
            timeoutTimer?.Dispose();
            timeoutTimer = new Timer(_ => OnTimeout(), null, RegistrationTimeoutMs, Timeout.Infinite);
        }

        private void OnTimeout()
        {
            lock (sync)
            {
                if (!registering)
                    return;
                registering = false;
                readingName = false;
            }
            LogMessage?.Invoke($"mfx-Anmeldung von {Hex(currentUid)} nicht abgeschlossen (keine Antwort der Lok).");
        }

        private void EndRegistration()
        {
            registering = false;
            readingName = false;
            timeoutTimer?.Dispose();
            timeoutTimer = null;
        }

        private void SendBind(byte[] uid, byte sid)
        {
            Buffer.BlockCopy(uid, 0, canFrames.M_BIND, 5, 4);
            canFrames.M_BIND[0x09] = 0;
            canFrames.M_BIND[0x0A] = sid;
            SendFrameRequested?.Invoke(canFrames.M_BIND);
        }

        private void SendVerify(byte[] uid, byte sid)
        {
            Buffer.BlockCopy(uid, 0, canFrames.M_VERIFY, 5, 4);
            canFrames.M_VERIFY[0x01] = CANFrames.MFXVerify_CMD;
            canFrames.M_VERIFY[0x09] = 0;
            canFrames.M_VERIFY[0x0A] = sid;
            SendFrameRequested?.Invoke(canFrames.M_VERIFY);
        }

        // Verify answer: the locomotive accepted the address
        private void OnVerifyAnswer(byte[] data)
        {
            byte[] uid = UidOf(data);
            if (Key(uid) == 0)
                return; // answer to a removal (Verify with UID 0)
            byte sid = data[0x0A];

            if (Ms2Present)
            {
                // the MS2 may bind once more (final address); wait until it is quiet
                lock (sync)
                {
                    ms2SettleUid = Key(uid);
                    ms2SettleTimer?.Dispose();
                    ms2SettleTimer = new Timer(_ => OnMs2Settled(), null, Ms2SettleMs, Timeout.Infinite);
                }
                return;
            }

            bool readName;
            lock (sync)
            {
                if (!registering || Key(uid) != Key(currentUid) || sid != currentSid)
                    return;
                readName = readingName;
                if (!readName)
                    EndRegistration();
            }
            if (readName)
                RequestName(sid);
            else
                LokReturned?.Invoke(Key(uid), sid);
        }

        // name of the locomotive: Read Config index 1, CV 3, 16 bytes
        private void RequestName(byte sid)
        {
            LogMessage?.Invoke("Lese den Namen der Lok ...");
            canFrames.ReadConfig[0x08] = sid;
            canFrames.ReadConfig[0x09] = 0x04;
            canFrames.ReadConfig[0x0A] = 0x03;
            canFrames.ReadConfig[0x0B] = 0x10;
            SendFrameRequested?.Invoke(canFrames.ReadConfig);
        }

        // ------------------------------------------------------------
        // Listening to the Mobile Station
        // ------------------------------------------------------------

        private void OnForeignBind(byte[] uid, byte sid)
        {
            if (!Ms2Present)
                return; // our own Bind comes back as well
            lock (sync)
                ms2Binds[Key(uid)] = sid;
        }

        // The MS2 has finished: take its last address and read the name (new locomotive)
        private void OnMs2Settled()
        {
            uint key;
            byte sid;
            lock (sync)
            {
                key = ms2SettleUid;
                if (!ms2Binds.TryGetValue(key, out sid) || registering)
                    return;
            }
            byte[] uid = BitConverter.GetBytes(key);
            byte? knownSid = FindKnownSid?.Invoke(key);
            if (knownSid == sid)
            {
                LokReturned?.Invoke(key, sid);
                return;
            }
            LogMessage?.Invoke($"Die Mobile Station hat mfx-Lok {Hex(uid)} die Adresse {sid} gegeben; die GUI übernimmt sie.");
            lock (sync)
                StartRegistration(uid, sid, readName: true);
            RequestName(sid);
        }

        // ------------------------------------------------------------
        // Name of the locomotive
        // ------------------------------------------------------------

        private void HandleMfxNameFrame(byte[] frame)
        {
            CANguruLok lok;
            lock (sync)
            {
                if (!registering || !readingName || frame[0x08] != currentSid)
                    return;
                lok = registry.GetOrCreate(Key(currentUid));
            }
            // one letter per answer; the index counts in steps of 4
            char letter = (char)frame[0x0B];
            int index = frame[0x09];
            int nr = Math.Max(0, (index - 4) / 4);
            lok.NameFramesReceived = nr;
            if (letter != '\0')
                lok.NameFragments[nr] = letter;
            if (nr >= CANguruLok.MaxNameFrames - 1)
                FinalizeLokName(lok);
        }

        private void FinalizeLokName(CANguruLok lok)
        {
            byte sid;
            lock (sync)
            {
                if (!registering)
                    return;
                sid = currentSid;
                EndRegistration();
            }
            var sb = new StringBuilder();
            foreach (var kv in lok.NameFragments.OrderBy(k => k.Key))
                sb.Append(kv.Value);
            lok.Name = sb.ToString().Trim();
            if (lok.Name.Length == 0)
                lok.Name = $"mfx-Lok {sid}";
            lok.IsManual = false;
            lok.MfxUID = (byte[])currentUid.Clone();
            lok.MfxSID = sid;
            LogMessage?.Invoke($"mfx-Lok angemeldet: „{lok.Name}“, Adresse {sid}.");
            LokCompleted?.Invoke(lok);
        }

        // ------------------------------------------------------------
        // Buttons
        // ------------------------------------------------------------

        // "mfx-Lok suchen": the same sequence as the MS2 - STOPP, registration counter,
        // enable all protocols, GO. The track box then searches for locomotives on the track.
        public void StartSearch()
        {
            LogMessage?.Invoke("Suche mfx-Loks (die Gleisspannung geht kurz aus) ...");
            SendFrameRequested?.Invoke(canFrames.SysSTOPP);
            SendCounter();
            SendFrameRequested?.Invoke(canFrames.GLEISBOX_ALL_PROTO_ENABLE);
            SendFrameRequested?.Invoke(canFrames.SysGO);
        }

        // "Lok entfernen" for an mfx locomotive, like the MS2: end the locomotive cycle,
        // release the address (Verify with UID 0), increase the registration counter.
        // A locomotive still on the track registers again at once.
        public void RemoveLok(CANguruLok lok)
        {
            byte sid = lok.MfxSID;
            if (sid != 0)
            {
                var stop = (byte[])canFrames.SysSTOPP.Clone();
                stop[0x07] = 0x40;
                stop[0x08] = sid;
                stop[0x09] = CANFrames.Lok_Zyklus_Stopp_CMD;
                SendFrameRequested?.Invoke(stop);
                SendVerify(new byte[4], sid);
            }
            canFrames.newSubscriptionCounter = (byte)(canFrames.newSubscriptionCounter >= 255 ? 1 : canFrames.newSubscriptionCounter + 1);
            CounterChanged?.Invoke(canFrames.newSubscriptionCounter);
            SendCounter();
            registry.Forget(lok.UID);
            lock (sync)
                ms2Binds.Remove(lok.UID);
            LogMessage?.Invoke($"mfx-Lok „{lok.Name}“ entfernt (Adresse {sid} frei, Neuanmeldezähler {canFrames.newSubscriptionCounter}).");
        }
    }
}
