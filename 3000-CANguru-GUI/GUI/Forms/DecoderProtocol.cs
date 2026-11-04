namespace CANguru.GUI.Forms
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using CANguru.Networking;
    using CANguru.Protocol;
    using CANguru.Views;

    // Anfragen an einen Decoder (Protokoll Version 2, über die Bridge an die Decoder-ID).
    // Antworten werden über Feature, Kommando und Folgenummer zugeordnet; zwei Decoder
    // gleichen Typs können sich dadurch nicht mehr ins Gehege kommen.
    public static class DecoderProtocol
    {
        // Designer-Erkennung
        private static bool IsDesigner =>
            LicenseManager.UsageMode == LicenseUsageMode.Designtime ||
            Process.GetCurrentProcess().ProcessName == "devenv";

        // Anfrage mit Antwort; null, wenn der Decoder nicht antwortet oder einen Fehler meldet
        public static byte[]? ReadPayload(byte decoderId, byte feature, byte command, int expectedLength, byte[]? requestPayload)
        {
            if (IsDesigner)
                return new byte[expectedLength]; // Dummy-Daten, damit Panels im Designer laden

            var r = BridgeLink.Instance.Request(feature, command, decoderId, requestPayload);
            if (r == null || r.IsError || r.Payload.Length < expectedLength)
                return null;
            return r.Payload;
        }

        // Befehl mit Quittung, ohne auf die Antwort zu warten. Kommt keine oder eine
        // Fehlerantwort, erscheint eine Meldung im Meldungsfenster.
        public static void Command(byte decoderId, byte feature, byte command, byte[]? payload)
        {
            if (IsDesigner)
                return;

            _ = Task.Run(async () =>
            {
                var r = await BridgeLink.Instance.RequestAsync(feature, command, decoderId, payload).ConfigureAwait(false);
                if (r == null)
                    Form1.Instance?.Log($"Decoder {decoderId} antwortet nicht.");
                else if (r.IsError)
                    Form1.Instance?.Log($"Decoder {decoderId}: {BridgeLink.ErrorText(r.ErrorCode)}");
            });
        }

        // Werkseinstellungen (Zubehöradresse bleibt erhalten); true = Decoder hat bestätigt
        public static bool FactoryReset(byte decoderId)
        {
            if (IsDesigner)
                return false;
            var r = BridgeLink.Instance.Request((byte)ProtocolConstants.CG2_F_PARAM,
                                                (byte)ProtocolConstants.CG2_PARAM_FACTORY_RESET, decoderId, null);
            return r != null && !r.IsError;
        }

        // Einstellungen eines Decoders neu lesen (nach Werkseinstellungen), mit Wiederholung
        public static byte[]? RequestAndReloadSettings(byte assignedId, byte decoderType)
        {
            byte feature;
            byte command;
            int expectedLength;

            switch (decoderType)
            {
                case ProtocolConstants.DEVTYPE_SWITCH:
                case ProtocolConstants.DEVTYPE_SIGNAL:
                    feature = (byte)ProtocolConstants.CG2_F_STEPPER;
                    command = (byte)ProtocolConstants.SWITCH_CMD_GET_SETTINGS;
                    expectedLength = ProtocolConstants.EXP_LNG_SWITCH_SETTINGS;
                    break;

                case ProtocolConstants.DEVTYPE_POWER:
                    return ReadPayload(assignedId, (byte)ProtocolConstants.CG2_F_POWER,
                                       (byte)ProtocolConstants.CG2_POWER_GET_THRESHOLD, 2, null);

                case ProtocolConstants.DEVTYPE_RM:
                    feature = (byte)ProtocolConstants.CG2_F_FEEDBACK;
                    command = (byte)ProtocolConstants.FEEDBACK_CMD_GET_SETTINGS;
                    expectedLength = ProtocolConstants.EXP_LNG_FEEDBACK_SETTINGS;
                    break;

                default:
                    return null;
            }

            // Der Decoder braucht nach den Werkseinstellungen etwas Zeit
            for (int i = 0; i < 3; i++)
            {
                var payload = ReadPayload(assignedId, feature, command, expectedLength, new byte[] { assignedId });
                if (payload != null)
                    return payload;
                Thread.Sleep(100);
            }
            return null;
        }

        // Grenzwert des Stromdecoders lesen (Little Endian)
        public static ushort ReadThreshold(byte decoderId, ushort defaultValue)
        {
            var p = ReadPayload(decoderId, (byte)ProtocolConstants.CG2_F_POWER,
                                (byte)ProtocolConstants.CG2_POWER_GET_THRESHOLD, 2, null);
            return p == null ? defaultValue : new Cg2Reader(p).U16();
        }
    }
}
