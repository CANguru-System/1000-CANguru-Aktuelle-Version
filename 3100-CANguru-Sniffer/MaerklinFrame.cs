using System.Text;

namespace CANguru.Sniffer
{
    // One Märklin CAN frame in the 13-byte format of the bridge:
    //   [0..3] CAN ID (big endian): prio, command << 1 | response bit, hash
    //   [4]    DLC
    //   [5..12] data
    // Names and field layout follow the public Märklin CS2 CAN protocol description.
    public sealed class MaerklinFrame
    {
        public byte[] Raw { get; }
        public int Prio => Raw[0] >> 1;
        public int Command => ((Raw[0] & 0x01) << 7) | (Raw[1] >> 1);
        public bool IsResponse => (Raw[1] & 0x01) != 0;
        public int Hash => (Raw[2] << 8) | Raw[3];
        public int Dlc => Math.Min((int)Raw[4], 8);
        public byte D(int i) => Raw[5 + i];

        public MaerklinFrame(byte[] raw13)
        {
            if (raw13.Length < 13)
                throw new ArgumentException("A CAN frame has 13 bytes.");
            Raw = raw13[..13];
        }

        public string DataHex()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Dlc; i++)
                sb.Append(i == 0 ? "" : " ").Append(D(i).ToString("X2"));
            return sb.ToString();
        }

        public string DataAscii()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Dlc; i++)
            {
                byte b = D(i);
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            return sb.ToString();
        }

        public string RawHex() => BitConverter.ToString(Raw).Replace("-", " ");

        // ------------------------------------------------------------
        // Names
        // ------------------------------------------------------------

        private static readonly Dictionary<int, string> commandNames = new()
        {
            [0x00] = "System",
            [0x01] = "mfx-Discovery",
            [0x02] = "mfx-Bind",
            [0x03] = "mfx-Verify",
            [0x04] = "Lok Geschwindigkeit",
            [0x05] = "Lok Richtung",
            [0x06] = "Lok Funktion",
            [0x07] = "Read Config",
            [0x08] = "Write Config",
            [0x0B] = "Zubehör schalten",
            [0x0C] = "Zubehör Konfiguration",
            [0x10] = "S88 Polling",
            [0x11] = "S88 Event",
            [0x12] = "SX1 Event",
            [0x18] = "Ping",
            [0x1B] = "Bootloader",
            [0x1C] = "Bootloader Gleis",
            [0x1D] = "Statusdaten Konfiguration",
            [0x20] = "Config-Daten anfordern",
            [0x21] = "Config-Datenstrom",
            [0x22] = "6021-Datenstrom",
        };

        private static readonly Dictionary<int, string> systemNames = new()
        {
            [0x00] = "STOPP",
            [0x01] = "GO",
            [0x02] = "Halt",
            [0x03] = "Lok-Nothalt",
            [0x04] = "Lok-Zyklus beenden",
            [0x05] = "Lok-Datenprotokoll",
            [0x06] = "Schaltzeit Zubehör",
            [0x07] = "Fast Read mfx",
            [0x08] = "Gleisprotokoll freischalten",
            [0x09] = "mfx-Neuanmeldezähler",
            [0x0A] = "Überlast",
            [0x0B] = "Status",
            [0x0C] = "Gerätekennung",
            [0x20] = "Systemzeit",
            [0x30] = "mfx Seek",
            [0x80] = "System-Reset",
        };

        private static readonly Dictionary<int, string> deviceNames = new()
        {
            [0x0000] = "GFP",
            [0x0010] = "Gleisbox",
            [0x0011] = "Gleisbox",
            [0x0020] = "Connect 6021",
            [0x0030] = "MS2",
            [0x0031] = "MS2",
            [0x0032] = "MS2",
            [0x0033] = "MS2",
            [0xEEEE] = "CS2-Software",
            [0xFFFF] = "CS2-GUI (Master)",
        };

        // CANguru devices answer the ping with their device type (0x4F..0x5F)
        private static string CanguruName(int dev) => dev switch
        {
            ProtocolConstants.DEVTYPE_SIGNAL => "CANguru Formsignal",
            ProtocolConstants.DEVTYPE_SWITCH => "CANguru Weiche",
            ProtocolConstants.DEVTYPE_POWER => "CANguru Stromdecoder",
            ProtocolConstants.DEVTYPE_RM => "CANguru Rückmelder",
            ProtocolConstants.DEVTYPE_LEDSIGNAL => "CANguru LED-Signal",
            ProtocolConstants.DEVTYPE_BRIDGE => "CANguru-Bridge",
            _ => $"CANguru-Gerät 0x{dev:X2}"
        };

        public string CommandName =>
            commandNames.TryGetValue(Command, out var n) ? n : $"Befehl 0x{Command:X2}";

        // ------------------------------------------------------------
        // Interpretation of the data bytes
        // ------------------------------------------------------------

        private uint U32(int i) => (uint)(D(i) << 24 | D(i + 1) << 16 | D(i + 2) << 8 | D(i + 3));
        private int U16(int i) => D(i) << 8 | D(i + 1);

        // Locomotive / accessory ID ("Lok-UID") as readable text
        public static string LocId(uint id)
        {
            if (id <= 0x03FF) return $"MM {id}";
            if (id >= 0x3000 && id <= 0x33FF) return $"MM-Zubehör {id - 0x3000 + 1}";
            if (id >= 0x3800 && id <= 0x3FFF) return $"DCC-Zubehör {id - 0x3800 + 1}";
            if (id >= 0x4000 && id <= 0x7FFF) return $"mfx SID {id - 0x4000}";
            if (id >= 0xC000 && id <= 0xFFFF) return id == 0xC000 ? "Programmiergleis (0xC000)" : $"DCC {id - 0xC000}";
            return $"UID {id:X8}";
        }

        public string Meaning()
        {
            try
            {
                return Interpret();
            }
            catch
            {
                return "";
            }
        }

        private string Interpret()
        {
            int n = Dlc;
            switch (Command)
            {
                case 0x00: // System: [UID 4][sub][...]
                {
                    if (n < 5)
                        return n >= 4 ? $"{LocId(U32(0))}" : "";
                    uint uid = U32(0);
                    int sub = D(4);
                    string target = uid == 0 ? "an alle" : LocId(uid);
                    string name = systemNames.TryGetValue(sub, out var s) ? s : $"Unterbefehl 0x{sub:X2}";
                    string extra = sub switch
                    {
                        0x05 when n >= 6 => $", Protokoll {D(5)}",
                        0x06 when n >= 7 => $", {U16(5) * 10} ms",
                        0x08 when n >= 6 => $", Protokolle 0x{D(5):X2}",
                        0x09 when n >= 7 => $", Zähler {U16(5)}",
                        0x0A when n >= 6 => $", Kanal {D(5)}",
                        0x0B when n >= 8 => $", Kanal {D(5)}, Wert {U16(6)}",
                        0x0B when n >= 6 => $", Kanal {D(5)}",
                        0x0C when n >= 7 => $", Kennung {U16(5)}",
                        0x30 when n >= 6 => $", {D(5)}",
                        _ => ""
                    };
                    return $"{name} {target}{extra}";
                }

                case 0x01: // mfx discovery
                    return n switch
                    {
                        0 => "Suche starten",
                        1 => $"Suche, Protokoll {D(0)}",
                        5 => $"UID {U32(0):X8}, Bereich {D(4)}",
                        6 => $"UID {U32(0):X8}, Bereich {D(4)}, Güte {D(5)}",
                        _ => ""
                    };

                case 0x02: // mfx bind: [UID 4][SID 2]
                    return n >= 6 ? $"UID {U32(0):X8} → SID {U16(4)}" : "";

                case 0x03: // mfx verify: [UID 4][SID 2]([ok])
                    if (n >= 7) return $"UID {U32(0):X8}, SID {U16(4)}, Ergebnis {D(6)}";
                    return n >= 6 ? $"UID {U32(0):X8}, SID {U16(4)}" : "";

                case 0x04: // speed: [loc 4]([speed 2])
                    if (n >= 6) return $"{LocId(U32(0))}, Fahrstufe {U16(4)}";
                    return n >= 4 ? $"{LocId(U32(0))} abfragen" : "";

                case 0x05: // direction: [loc 4]([dir])
                    if (n >= 5)
                    {
                        string dir = D(4) switch { 0 => "beibehalten", 1 => "vorwärts", 2 => "rückwärts", 3 => "umschalten", _ => D(4).ToString() };
                        return $"{LocId(U32(0))}, Richtung {dir}";
                    }
                    return n >= 4 ? $"{LocId(U32(0))} abfragen" : "";

                case 0x06: // function: [loc 4][fn]([value])
                    if (n >= 6) return $"{LocId(U32(0))}, F{D(4)} = {D(5)}";
                    return n >= 5 ? $"{LocId(U32(0))}, F{D(4)} abfragen" : "";

                case 0x07: // read config: [loc 4][index/CV 2][count | value]
                case 0x08: // write config: [loc 4][index/CV 2][value][control]
                {
                    if (n < 6)
                        return n >= 4 ? LocId(U32(0)) : "";
                    int index = D(4) >> 2;
                    int cv = ((D(4) & 0x03) << 8) | D(5);
                    string where = index == 0 ? $"CV {cv}" : $"Index {index}, CV {cv}";
                    string what = "";
                    if (Command == 0x07 && n >= 7)
                        what = IsResponse ? $", Wert {D(6)} (0x{D(6):X2})" : $", Anzahl {D(6)}";
                    else if (Command == 0x08 && n >= 7)
                        what = $", Wert {D(6)} (0x{D(6):X2})" + (n >= 8 ? $", Steuerung 0x{D(7):X2}" : "");
                    return $"{LocId(U32(0))}, {where}{what}";
                }

                case 0x0B: // accessory: [loc 4][position][current]([time 2])
                    if (n >= 6)
                    {
                        string pos = D(4) switch { 0 => "rot/rund", 1 => "grün/gerade", _ => $"Stellung {D(4)}" };
                        return $"{LocId(U32(0))}, {pos}, Strom {D(5)}";
                    }
                    return n >= 4 ? LocId(U32(0)) : "";

                case 0x11: // S88 event: [device 2][contact 2][old][new]([time 2])
                    if (n >= 6) return $"Gerät {U16(0)}, Kontakt {U16(2)}: {D(4)} → {D(5)}";
                    return n >= 4 ? $"Gerät {U16(0)}, Kontakt {U16(2)} abfragen" : "";

                case 0x18: // ping: answer [UID 4][version 2][device 2]
                    if (n >= 8)
                    {
                        int dev = U16(6);
                        string name = deviceNames.TryGetValue(dev, out var d) ? d
                            : dev >= ProtocolConstants.DEVTYPE_FirstCANguru && dev <= ProtocolConstants.DEVTYPE_LastCANguru
                                ? CanguruName(dev)
                                : $"Gerät 0x{dev:X4}";
                        return $"{name}, UID {U32(0):X8}, Version {D(4)}.{D(5)}";
                    }
                    return n == 0 ? "Wer ist da?" : "";

                case 0x1B: // bootloader (also the wake-up of the track box)
                    return n >= 5 ? $"Ziel {U32(0):X8}, Daten 0x{D(4):X2}" : "";

                case 0x1D: // status data configuration: [UID 4][index]([count])
                    if (n >= 6) return $"UID {U32(0):X8}, Index {D(4)}, Pakete {D(5)}";
                    return n >= 5 ? $"UID {U32(0):X8}, Index {D(4)}" : "";

                case 0x20: // request config data: file name in ASCII
                    return $"Datei \"{DataAscii().TrimEnd('.')}\"";

                case 0x21: // config data stream: header (length, CRC) or 8 data bytes
                    if (n == 6 || n == 7) return $"Länge {U32(0)}, CRC 0x{U16(4):X4}";
                    return n == 8 ? $"Daten \"{DataAscii()}\"" : "";
            }
            return "";
        }
    }
}
