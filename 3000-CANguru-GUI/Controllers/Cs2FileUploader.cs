using System;
using System.IO;
using System.Threading.Tasks;
using CANguru.Networking;
using CANguru.Protocol;

namespace CANguru
{
    // Überträgt lokomotive.cs2 zur Bridge (Protokoll Version 2, Feature Datei):
    // BLOCK [offset][daten] mit Quittung je Block, am Ende END [größe][crc32].
    // WinDigiPet holt die Datei anschließend per HTTP von der Bridge.
    public class Cs2FileUploader
    {
        private const int BlockSize = 1024;
        private bool _laeuft;

        public event Action<int, int>? ProgressChanged;   // (übertragen, gesamt)
        public event Action? UploadCompleted;
        public event Action<string>? UploadError;

        // true erst, wenn die Bridge die vollständige Datei bestätigt hat
        public async Task<bool> StartUploadAsync(string filePath)
        {
            if (_laeuft)
                throw new InvalidOperationException("Es läuft bereits ein Upload.");
            _laeuft = true;
            try
            {
                if (!File.Exists(filePath))
                    return Fehler("Datei nicht gefunden: " + filePath);

                byte[] data = File.ReadAllBytes(filePath);

                // UTF-8-BOM entfernen (bisher tat das die Bridge)
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                    data = data[3..];

                var link = BridgeLink.Instance;
                byte bridge = (byte)ProtocolConstants.CG2_ID_BRIDGE;
                byte fFile = (byte)ProtocolConstants.CG2_F_FILE;

                int offset = 0;
                do
                {
                    int len = Math.Min(BlockSize, data.Length - offset);
                    byte[] block = new byte[len];
                    Array.Copy(data, offset, block, 0, len);
                    byte[] payload = new Cg2Writer().U32((uint)offset).Bytes(block).ToArray();

                    var r = await link.RequestAsync(fFile, (byte)ProtocolConstants.CG2_FILE_BLOCK, bridge, payload);
                    if (r == null)
                        return Fehler($"Bridge bestätigt Block bei {offset} nicht.");
                    if (r.IsError)
                        return Fehler($"Bridge meldet bei {offset}: {BridgeLink.ErrorText(r.ErrorCode)}");

                    offset += len;
                    ProgressChanged?.Invoke(offset, data.Length);
                } while (offset < data.Length);

                uint crc = Cg2.Crc32(0, data, 0, data.Length);
                byte[] ende = new Cg2Writer().U32((uint)data.Length).U32(crc).ToArray();
                var end = await link.RequestAsync(fFile, (byte)ProtocolConstants.CG2_FILE_END, bridge, ende);
                if (end == null || end.IsError || end.Payload.Length < 1 || end.Payload[0] != 1)
                    return Fehler("Bridge hat die Datei nicht vollständig erhalten (Größe oder Prüfsumme falsch).");

                UploadCompleted?.Invoke();
                return true;
            }
            finally
            {
                _laeuft = false;
            }
        }

        private bool Fehler(string msg)
        {
            UploadError?.Invoke(msg);
            return false;
        }
    }
}
