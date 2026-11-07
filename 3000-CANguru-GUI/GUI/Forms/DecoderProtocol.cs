namespace CANguru.GUI.Forms
{
    using System.ComponentModel;
    using System.Diagnostics;
    using CANguru.Networking;
    using CANguru.Protocol;

    // Blockierende Anfragen an einen Decoder (Protokoll Version 2, über die Bridge an die
    // Decoder-ID), z. B. für die Angaben in der Decoderliste. Das Parameter-Fenster
    // arbeitet asynchron direkt mit BridgeLink.
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
                return null;

            var r = BridgeLink.Instance.Request(feature, command, decoderId, requestPayload);
            if (r == null || r.IsError || r.Payload.Length < expectedLength)
                return null;
            return r.Payload;
        }

        // Einen Parameter lesen (Feature Parameter, READ [nummer] -> [nummer][wert u16]);
        // null, wenn der Decoder nicht antwortet oder die Nummer nicht kennt
        public static ushort? ReadParam(byte decoderId, byte number)
        {
            var p = ReadPayload(decoderId, (byte)ProtocolConstants.CG2_F_PARAM,
                                (byte)ProtocolConstants.CG2_PARAM_READ, 3, new[] { number });
            return p == null ? null : new Cg2Reader(p[1..]).U16();
        }
    }
}
