using CANguru.GUI.Forms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CANguru.Models
{
    public enum DecoderType
    {
        Signal = ProtocolConstants.DEVTYPE_SIGNAL,
        Switch = ProtocolConstants.DEVTYPE_SWITCH,
        Power = ProtocolConstants.DEVTYPE_POWER,
        Feedback = ProtocolConstants.DEVTYPE_RM,
        LedSignal = ProtocolConstants.DEVTYPE_LEDSIGNAL
    }

    // Names of the decoder types for lists, counters and the OTA choice. A type the GUI
    // does not know yet is shown with its number, so a new decoder type appears anyway.
    public static class DecoderTypeNames
    {
        public static string Singular(int devType) => devType switch
        {
            ProtocolConstants.DEVTYPE_SWITCH => "Weiche",
            ProtocolConstants.DEVTYPE_SIGNAL => "Formsignal",
            ProtocolConstants.DEVTYPE_POWER => "Stromdecoder",
            ProtocolConstants.DEVTYPE_RM => "Rückmelder",
            ProtocolConstants.DEVTYPE_LEDSIGNAL => "LED-Signal",
            _ => $"Decoder 0x{devType:X2}"
        };

        public static string Plural(int devType) => devType switch
        {
            ProtocolConstants.DEVTYPE_SWITCH => "Weichen",
            ProtocolConstants.DEVTYPE_SIGNAL => "Formsignale",
            ProtocolConstants.DEVTYPE_POWER => "Strom",
            ProtocolConstants.DEVTYPE_RM => "Rückmelder",
            ProtocolConstants.DEVTYPE_LEDSIGNAL => "LED-Signale",
            _ => $"Typ 0x{devType:X2}"
        };
    }

    public abstract class CANguruDevice
    {
        // Von der Bridge vergebene ID
        public byte AssignedId { get; set; }

        // Eindeutige Identität (MAC-Adresse)
        public byte[] Mac { get; set; } = new byte[6];

        // Anzeigename (GUI)
        public string Name { get; set; } = string.Empty;

        // Alive-Überwachung
        public DateTime LastSeen { get; set; } = DateTime.MinValue;

        // READY-Status nach Handshake
        public bool IsReady { get; set; } = false;

        // Protokollversion
        public int ProtocolVersion { get; set; } = 1;

        // Statuswerte (z. B. Ports, Rückmelder, Funktionen)
        public Dictionary<int, int> ChannelStates { get; set; } = new();

    }

    // Erreichbarkeit eines Decoders laut Alive: unter 2 s, unter 5 s, länger
    public enum DecoderStatus
    {
        Healthy,
        Weak,
        Lost
    }

    // Ein angemeldeter CANguru-Decoder. Schlüssel ist die MAC-Adresse (seit Phase 2 behält
    // ein Decoder auch seine ID dauerhaft). AssignedId, Mac und Name kommen aus CANguruDevice.
    public class CANguruDecoder : CANguruDevice, IDecoderListViewItem
    {
        // Typ des Decoders (Weiche, Formsignal, Strom, Rückmelder, LED-Signal)
        public DecoderType Type { get; set; }

        // Firmware aus der Anmeldung, z. B. "2.1"
        public string Firmware { get; set; } = string.Empty;

        // Erreichbarkeit, berechnet aus LastSeen (Alive)
        public DecoderStatus Status { get; set; } = DecoderStatus.Lost;

        public string MacText => string.Join(":", Mac.Select(b => b.ToString("X2")));

        // Bezeichnung des Typs für Listen und Fenstertitel
        public virtual string TypeText => DecoderTypeNames.Singular((int)Type);

        // eigener Name des Nutzers, sonst der Typ
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? TypeText : Name;

        // Newest firmware of this type known to this GUI build (central table in
        // 0300-Constants/constants_manager.py); null = unknown type
        public (int Major, int Minor)? LatestFirmware => Type switch
        {
            DecoderType.Switch or DecoderType.Signal => (ProtocolConstants.FW_STEPPER_MAJOR, ProtocolConstants.FW_STEPPER_MINOR),
            DecoderType.LedSignal => (ProtocolConstants.FW_LEDSIGNAL_MAJOR, ProtocolConstants.FW_LEDSIGNAL_MINOR),
            DecoderType.Power => (ProtocolConstants.FW_POWER_MAJOR, ProtocolConstants.FW_POWER_MINOR),
            DecoderType.Feedback => (ProtocolConstants.FW_FEEDBACK_MAJOR, ProtocolConstants.FW_FEEDBACK_MINOR),
            _ => null
        };

        public string LatestFirmwareText => LatestFirmware is { } v ? $"{v.Major}.{v.Minor}" : "";

        // true = the decoder runs older firmware than this build offers (update by OTA)
        public bool FirmwareOutdated
        {
            get
            {
                if (LatestFirmware is not { } latest)
                    return false;
                var parts = Firmware.Split('.');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
                    return false;
                return major < latest.Major || (major == latest.Major && minor < latest.Minor);
            }
        }

        // Angaben je Typ für die Spalte "Details" der Decoder-Liste
        protected virtual string Details => "";

        // Zubehöradresse(n) für die Decoderliste im Hauptfenster, z. B. "12" oder "20–25"
        public virtual string AddressText => "";

        public (string Name, string Type, string Mac, string Id, string Details) ToListViewColumns() =>
            (DisplayName, TypeText, MacText, AssignedId.ToString(), Details);

        // Adressen kompakt: "20–25" bei lückenloser Folge, sonst "20, 22, 24"
        protected static string Compact(IEnumerable<int> values)
        {
            var v = values.Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
            if (v.Count == 0)
                return "";
            if (v.Count > 2 && v[^1] - v[0] == v.Count - 1)
                return $"{v[0]}–{v[^1]}";
            return string.Join(", ", v);
        }

        // Einstellungen für die Listen vom Decoder lesen (blockierend, im Hintergrund aufrufen)
        public virtual void UpdateFromDecoder()
        {
        }
    }

    // Weiche und Formsignal (Zubehör-Decoder mit Stepper): Parameter 0 Adresse,
    // 1 Verzögerung, 2 Laufweg
    public class StepperDecoder : CANguruDecoder
    {
        public byte Address { get; set; }
        public int Delay { get; set; }
        public int StepsToEnd { get; set; }

        public override void UpdateFromDecoder()
        {
            var adr = DecoderProtocol.ReadParam(AssignedId, 0);
            if (adr == null)
                return; // Decoder noch nicht bereit
            Address = (byte)adr.Value;
            Delay = DecoderProtocol.ReadParam(AssignedId, 1) ?? Delay;
            StepsToEnd = DecoderProtocol.ReadParam(AssignedId, 2) ?? StepsToEnd;
        }

        protected override string Details => $"Adresse {Address}, Verzögerung {Delay}, Laufweg {StepsToEnd}";

        public override string AddressText => Address > 0 ? Address.ToString() : "";
    }

    // LED-Signaldecoder (PCA9685, bis zu 8 Signale). Parameter je Signal k (0 ... 7):
    // 2k = Adresse, 2k + 1 = vorhanden
    public class LedSignalDecoder : CANguruDecoder
    {
        public const int Signals = 8;
        public byte[] Addresses = new byte[Signals];
        public bool[] Present = new bool[Signals];

        public override void UpdateFromDecoder()
        {
            for (int k = 0; k < Signals; k++)
            {
                var adr = DecoderProtocol.ReadParam(AssignedId, (byte)(2 * k));
                var on = DecoderProtocol.ReadParam(AssignedId, (byte)(2 * k + 1));
                if (adr == null || on == null)
                    return; // Decoder noch nicht bereit
                Addresses[k] = (byte)adr.Value;
                Present[k] = on.Value != 0;
            }
        }

        protected override string Details
        {
            get
            {
                var used = Enumerable.Range(0, Signals).Where(k => Present[k])
                                     .Select(k => $"S{k + 1}:{Addresses[k]}").ToList();
                return used.Count == 0 ? "keine Signale angeschlossen"
                                       : $"{used.Count} Signale, Adressen " + string.Join(" ", used);
            }
        }

        public override string AddressText =>
            Compact(Enumerable.Range(0, Signals).Where(k => Present[k]).Select(k => (int)Addresses[k]));
    }

    // Stromdecoder: Parameter 0 = Abschaltgrenze in mA
    public class PowerDecoder : CANguruDecoder
    {
        public ushort MaxCurrent { get; set; }

        public override void UpdateFromDecoder()
        {
            MaxCurrent = DecoderProtocol.ReadParam(AssignedId, 0) ?? MaxCurrent;
        }

        protected override string Details => $"Abschaltgrenze {MaxCurrent} mA";
    }

    // Rückmelder: Parameter 0 ... 15 = Meldeadresse der Kontakte 1 ... 16
    public class FeedbackDecoder : CANguruDecoder
    {
        public byte[] Channels = new byte[16];

        public override void UpdateFromDecoder()
        {
            for (byte i = 0; i < 16; i++)
            {
                var v = DecoderProtocol.ReadParam(AssignedId, i);
                if (v == null)
                    return; // Decoder noch nicht bereit
                Channels[i] = (byte)v.Value;
            }
        }

        protected override string Details =>
            "K1–K8: " + string.Join(" ", Channels.Take(8).Select(v => v.ToString("D2"))) +
            " | K9–K16: " + string.Join(" ", Channels.Skip(8).Take(8).Select(v => v.ToString("D2")));

        public override string AddressText => Compact(Channels.Select(c => (int)c));
    }

    public class LokFunktion
    {
        public int Nr { get; set; }      // Funktionsnummer (0–28)
        public int? Typ { get; set; }    // Funktions-Typ (optional)
    }

    public class CANguruLok : CANguruDevice
    {
        // Allgemeine Lokdaten
        public string Name { get; set; } = string.Empty;

        // UID = echte UID (mfx) oder künstliche UID (manuell)
        public uint UID { get; set; }

        // MfxUID:
        // - mfx-Lok: 4 Byte echte UID
        // - manuelle Lok: 1 Byte (Adresse)
        public byte[] MfxUID { get; set; } = Array.Empty<byte>();

        // SID / Adresse
        public byte MfxSID { get; set; }

        // Loktyp
        // mfx-Lok: IsManual = false, DecoderType = ""
        // manuelle Lok: IsManual = true, DecoderType = "MM1" oder "MM2"
        public bool IsManual { get; set; } = false;
        public string DecoderType { get; set; } = string.Empty;
        public Dictionary<int, char> NameFragments { get; set; } = new();
        public int NameFramesReceived { get; set; } = 0;

        public const int MaxNameFrames = 16;

        // CS2-Parameter
        public string Icon { get; set; } = string.Empty;

        public int Av { get; set; } = 60;      // Anfahrverzögerung
        public int Bv { get; set; } = 40;      // Bremsverzögerung
        public int Volume { get; set; } = 100; // Lautstärke
        public int ProgMask { get; set; } = 0x3;
        public int Vmin { get; set; } = 13;
        public int Vmax { get; set; } = 100;
        public int XProt { get; set; } = 2;

        // Funktionsliste (F0–F28 etc.)
        public List<LokFunktion> Funktionen { get; set; } = new();

        /*       public string DetailsString()
               {
                   return $"SpeedSteps:{SpeedSteps}  Functions:{FunctionCount}";
               }
        {name} {adr} {typ} {uid}*/

    }
}
