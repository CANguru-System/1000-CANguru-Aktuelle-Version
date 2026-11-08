using CANguru.Networking;

namespace CANguru.OTA
{
    // Firmware update of decoders: upload the file to the bridge, start the update for one
    // decoder or all decoders of a type, and evaluate the progress and result messages.
    // The window only shows what this class reports.
    public sealed class OtaService
    {
        public event Action<string>? Log;
        // percent of the current decoder, overall percent
        public event Action<int, int>? ProgressChanged;
        // all decoders processed: hide the progress bar
        public event Action? Finished;
        // decoder id, reason, true = decoder still runs the old firmware
        public event Action<byte, string, bool>? Failed;

        private readonly OtaUploader uploader;
        private int lastDecoder = -1; // decoder whose progress came last
        private int lastPercent = -1; // last reported progress of that decoder

        // uploadPercent: progress of the upload to the bridge (0..100)
        public OtaService(Action<int> uploadPercent)
        {
            uploader = new OtaUploader(log: text => Log?.Invoke(text), setPercent: uploadPercent,
                                       blockSize: ProtocolConstants.CG2_OTA_BLOCK_SIZE);
        }

        // Upload the firmware to the bridge. false = failed or cancelled (already logged).
        public async Task<bool> UploadAsync(byte[] firmware, CancellationToken ct)
        {
            try
            {
                return await uploader.UploadFirmwareAsync(firmware, ct);
            }
            catch (OperationCanceledException)
            {
                Log?.Invoke("Firmware-Upload abgebrochen.");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Firmware-Upload Fehler: {ex.Message}");
            }
            return false;
        }

        // Start the update: one decoder (single = true, selector = id) or all decoders of a
        // type (selector = DEVTYPE_*). Returns null on success, otherwise the reason.
        public async Task<string?> StartAsync(bool single, byte selector)
        {
            lastDecoder = -1;
            byte mode = single ? (byte)0 : (byte)1;
            var start = await BridgeLink.Instance.RequestAsync(
                (byte)ProtocolConstants.CG2_F_OTA, (byte)ProtocolConstants.CG2_OTA_START,
                (byte)ProtocolConstants.CG2_ID_BRIDGE, new byte[] { mode, selector });

            if (start != null && !start.IsError)
                return null;
            return start == null ? "Die Bridge antwortet nicht."
                : start.ErrorCode == ProtocolConstants.CG2_ERR_UNKNOWN_DECODER
                    ? (single ? "Der Decoder ist der Bridge nicht bekannt." : "Kein Decoder dieses Typs angemeldet.")
                    : BridgeLink.ErrorText(start.ErrorCode);
        }

        // OTA_PROGRESS from the bridge
        public void OnProgress(int id, int percent, int overall)
        {
            // 100 % overall = all decoders processed (also after a failure)
            if (overall >= 100 && percent < 100)
            {
                Finished?.Invoke();
                return;
            }

            if (id != lastDecoder)
            {
                lastDecoder = id;
                lastPercent = -1;
                Log?.Invoke($"Decoder {id} lädt Firmware ...");
            }
            else if (percent == 0 && lastPercent > 0)
                Log?.Invoke($"Decoder {id}: neuer Versuch ...");
            bool transferred = percent == 100 && lastPercent != 100;
            lastPercent = percent;

            ProgressChanged?.Invoke(percent, overall);
            if (transferred)
                // whether the update succeeded is known only after check and restart (OTA_RESULT)
                Log?.Invoke("Firmware an den Decoder übertragen – Decoder prüft und startet neu ...");
            if (overall >= 100)
                Finished?.Invoke();
        }

        // OTA_RESULT from the bridge: result 0 = ok, 1 = failed, 2 = no answer
        public void OnResult(byte decoderId, byte result, int reason)
        {
            if (result == 0)
            {
                Log?.Invoke($"OTA erfolgreich für Decoder {decoderId}.");
                return;
            }

            string grund = result != 1
                ? "Der Decoder hat sich nicht zurückgemeldet."
                : reason switch
                {
                    ProtocolConstants.CG2_OTA_FAIL_WIFI => "Der Decoder konnte sich nicht mit dem WLAN der Bridge verbinden.",
                    ProtocolConstants.CG2_OTA_FAIL_HTTP => "Der Decoder konnte die Datei nicht von der Bridge abrufen.",
                    ProtocolConstants.CG2_OTA_FAIL_SIZE => "Die Datei ist zu groß für den Decoder.",
                    ProtocolConstants.CG2_OTA_FAIL_DOWNLOAD => "Der Download wurde unterbrochen (Datei unvollständig).",
                    ProtocolConstants.CG2_OTA_FAIL_INVALID => "Die Datei ist keine gültige Firmware für diesen Decoder.",
                    _ => "Das Update ist fehlgeschlagen."
                };
            if (result == 1 && reason != ProtocolConstants.CG2_OTA_FAIL_INVALID && reason != ProtocolConstants.CG2_OTA_FAIL_SIZE)
                grund += " Die Bridge hat es bereits zweimal versucht.";
            Log?.Invoke($"OTA FEHLGESCHLAGEN für Decoder {decoderId}: {grund}");
            Failed?.Invoke(decoderId, grund, result == 1);
        }
    }
}
