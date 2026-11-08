using System.Collections.Concurrent;
using System.Text;

namespace CANguru.Controllers
{
    // CAN monitor: collects frames from the receive thread, hands them out sorted by
    // sequence number and turns a frame into a display line.
    // Frame layout (14 bytes): [prio][cmd][hash hi][hash lo][dlc][data 8][source]
    public sealed class CanMonitor
    {
        private readonly ConcurrentQueue<(uint seq, byte[] data)> queue = new();

        public void Enqueue(uint seq, byte[] frame) => queue.Enqueue((seq, frame));

        // All frames received so far, in the order the bridge sent them
        public List<byte[]> TakeSorted()
        {
            var list = new List<(uint seq, byte[] data)>();
            while (queue.TryDequeue(out var item))
                list.Add(item);
            list.Sort((a, b) => a.seq.CompareTo(b.seq));
            return list.Select(f => f.data).ToList();
        }

        // Frames of the mfx registration (commands 0x01..0x07) are also shown in the mfx list
        public static bool IsMfxFrame(byte[] data) => data[1] > 0x00 && data[1] < 0x08;

        public static string Format(byte[] data)
        {
            byte currCMD = data[0x01];
            byte subCmd = data[0x09];
            byte dlc = data[0x04];
            char source = (char)data[0x0D];

            var sb = new StringBuilder();
            sb.AppendFormat("{0}-{1:X2}({2:X2}){3:X2}{4:X2}", source, data[0], currCMD, data[2], data[3]);
            sb.Append((currCMD & 0x01) == 0x01 ? " R " : "   ");
            sb.AppendFormat("[{0}]", dlc);

            for (int i = 5; i < 5 + dlc; i++)
                sb.AppendFormat(" {0:X2}", data[i]);

            if (dlc < 8)
            {
                sb.AppendFormat("({0:X2}", data[5 + dlc]);
                for (int i = 6 + dlc; i < 13; i++)
                    sb.AppendFormat(" {0:X2}", data[i]);
                sb.Append(") ");
            }
            else
            {
                sb.Append("  ");
            }

            sb.Append("  ");

            char[] chars = Encoding.UTF8.GetChars(data, 5, 8);
            for (int i = 0; i < 8 && i < chars.Length; i++)
                sb.Append(chars[i] < ' ' || chars[i] > 'z' ? '.' : chars[i]);

            string text = sb.ToString() + CommandName(currCMD, subCmd);

            // unknown commands are cut to a fixed length (as before)
            const int maxString = 60;
            if (!IsKnown(currCMD) && text.Length > maxString)
                text = text.Substring(0, maxString);
            return text;
        }

        private static bool IsKnown(byte cmd) => cmd == 0x00 || NamedCommands.ContainsKey(cmd);

        private static readonly Dictionary<byte, string> SystemCommands = new()
        {
            [0x00] = "Sys STOPP", [0x01] = "Sys GO", [0x02] = "Sys Halt", [0x03] = "Lok Halt",
            [0x04] = "Lok Stop", [0x05] = "Lok Daten", [0x06] = "Schaltzeit", [0x07] = "Fast Read",
            [0x08] = "Glsproto", [0x09] = "Anm.zähler", [0x0A] = "Überlast", [0x0B] = "Status",
            [0x0C] = "Kennung", [0x30] = "Mfx Seek", [0x80] = "Reset",
        };

        private static readonly Dictionary<byte, string> NamedCommands = new()
        {
            [0x02] = "Discovery", [0x04] = "MFX Bind", [0x06] = "MFX Verify", [0x08] = "Lok Speed",
            [0x0A] = "Richtung", [0x0C] = "Funktion", [0x0E] = "Read Config", [0x10] = "Write Config",
            [0x16] = "Zub Schalten", [0x18] = "Zub Konfig", [0x20] = "S88 Feedback", [0x22] = "S88 Event",
            [0x24] = "SX1 Event", [0x30] = "Ping", [0x32] = "Update", [0x34] = "Config Data",
            [0x36] = "Magic", [0x38] = "Bootloader", [0x3A] = "Send Konfi", [0x40] = "Data Query",
            [0x42] = "Data Stream", [0x44] = "6021 adapter",
        };

        private static string CommandName(byte cmd, byte subCmd)
        {
            if (cmd == 0x00)
                return SystemCommands.TryGetValue(subCmd, out var sys) ? sys : "";
            if (NamedCommands.TryGetValue(cmd, out var name))
                return name;
            return cmd % 2 == 0 ? "unbekannt" : $"RE: {cmd:X2}";
        }
    }
}
