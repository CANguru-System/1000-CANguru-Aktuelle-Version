using System;
using System.Threading;
using System.Threading.Tasks;
using CANguru.Networking;
using CANguru.Protocol;

namespace CANguru.OTA
{
    // Lädt eine Firmware-Datei zur Bridge (Protokoll Version 2, Feature OTA):
    // UPLOAD_BEGIN [größe][crc32], dann UPLOAD_BLOCK [offset][daten] mit Quittung je Block.
    // Die Bridge prüft am Ende Größe und CRC-32 der ganzen Datei.
    public class OtaUploader
    {
        private readonly Action<string> _log;
        private readonly Action<int> _setPercent;   // 0..100
        private readonly int _blockSize;

        public OtaUploader(Action<string> log, Action<int> setPercent, int blockSize)
        {
            _log = log;
            _setPercent = setPercent;
            _blockSize = blockSize;
        }

        public async Task<bool> UploadFirmwareAsync(byte[] firmware, CancellationToken ct)
        {
            var link = BridgeLink.Instance;
            byte bridge = (byte)ProtocolConstants.CG2_ID_BRIDGE;
            byte fOta = (byte)ProtocolConstants.CG2_F_OTA;

            uint crc = Cg2.Crc32(0, firmware, 0, firmware.Length);
            byte[] begin = new Cg2Writer().U32((uint)firmware.Length).U32(crc).ToArray();

            _log("Firmware-Upload zur Bridge beginnt ...");
            var r = await link.RequestAsync(fOta, (byte)ProtocolConstants.CG2_OTA_UPLOAD_BEGIN, bridge, begin, 1000, ct);
            if (r == null || r.IsError)
            {
                _log("Bridge nimmt keine Firmware an: " +
                     (r == null ? "keine Antwort" : BridgeLink.ErrorText(r.ErrorCode)));
                return false;
            }

            int offset = 0;
            while (offset < firmware.Length)
            {
                ct.ThrowIfCancellationRequested();

                int len = Math.Min(_blockSize, firmware.Length - offset);
                byte[] block = new byte[len];
                Buffer.BlockCopy(firmware, offset, block, 0, len);
                byte[] payload = new Cg2Writer().U32((uint)offset).Bytes(block).ToArray();

                r = await link.RequestAsync(fOta, (byte)ProtocolConstants.CG2_OTA_UPLOAD_BLOCK, bridge, payload, 500, ct);
                if (r == null)
                {
                    _log($"Upload abgebrochen: Bridge bestätigt Block bei {offset} nicht.");
                    return false;
                }
                if (r.IsError)
                {
                    bool letzterBlock = offset + len >= firmware.Length;
                    _log(letzterBlock && r.ErrorCode == ProtocolConstants.CG2_ERR_BAD_VALUE
                        ? "Upload fehlgeschlagen: Prüfsumme der Firmware auf der Bridge falsch."
                        : $"Upload abgebrochen bei {offset}: {BridgeLink.ErrorText(r.ErrorCode)}");
                    return false;
                }

                offset += len;
                _setPercent((int)(offset * 100L / firmware.Length));
            }

            _log("Firmware-Upload zur Bridge abgeschlossen.");
            return true;
        }
    }
}
