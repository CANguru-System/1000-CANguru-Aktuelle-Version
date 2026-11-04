using CANguru.GUI.Forms;
using CANguru.Networking;
using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace CANguru.GUI.Forms
{
    public partial class FeedbackDecoderSettingsForm : Form
    {
        private readonly byte assignedId;

        public FeedbackDecoderSettingsForm(byte decoderId)
        {
            InitializeComponent();
            assignedId = decoderId;
        }

        private void FeedbackDecoderSettingsForm_Load(object sender, EventArgs e)
        {
            // Werte vom Decoder laden
            RequestFeedbackSettings();
        }

        public void HandleGetSettingsAck(byte featureId, byte commandId, byte[] payload)
        {
            if (payload == null || payload.Length != 16)
                return;

            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() =>
                {
                    feedbackPanel.LoadSettings(payload);
                }));
            }
            else
            {
                feedbackPanel.LoadSettings(payload);
            }
        }
        public void HandleSetSettingsAck(byte featureId, byte commandId, byte[] payload)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show("Werte erfolgreich gespeichert.");
                }));
            }
            else
            {
                MessageBox.Show("Werte erfolgreich gespeichert.");
            }
        }

        // Anfrage an den Rückmelder (Feature Rückmelder, Nutzdaten[0] = Decoder-ID)
        private async Task<Cg2Reply?> RequestAsync(byte command, params byte[] payload)
        {
            var fullPayload = new List<byte> { assignedId };
            fullPayload.AddRange(payload);
            return await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_FEEDBACK, command,
                                                          assignedId, fullPayload.ToArray());
        }

        private async void RequestFeedbackSettings()
        {
            var r = await RequestAsync((byte)ProtocolConstants.FEEDBACK_CMD_GET_SETTINGS);
            if (r == null || r.IsError)
            {
                MessageBox.Show(this, "Der Rückmelder antwortet nicht.", "Rückmelder",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            HandleGetSettingsAck(r.Header.Feature, r.Header.Command, r.Payload);
        }

        private async void btnSave_Click(object sender, EventArgs e)
        {
            // Panel → Payload bauen, GUI → Bridge → Decoder
            byte[] payload = feedbackPanel.BuildPayload();
            var r = await RequestAsync((byte)ProtocolConstants.FEEDBACK_CMD_SET_SETTINGS, payload);
            if (r == null || r.IsError)
            {
                MessageBox.Show(this, "Die Werte wurden nicht gespeichert: " +
                                (r == null ? "der Rückmelder antwortet nicht." : BridgeLink.ErrorText(r.ErrorCode)),
                                "Rückmelder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            HandleSetSettingsAck(r.Header.Feature, r.Header.Command, r.Payload);
        }
        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }
        private void btnReset_Click(object sender, EventArgs e)
        {
            // Defaultwerte direkt ins Panel schreiben
            feedbackPanel.LoadSettings(new byte[]
            {
        0x01, 0x02, 0x03, 0x04,
        0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C,
        0x0D, 0x0E, 0x0F, 0x10
            });
        }
    }
}
