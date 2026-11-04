using CANguru.GUI.Forms;
using CANguru.GUI.Panels;
using CANguru.Models;
using CANguru.Networking;
using CANguru.Protocol;
using CANguru.Views;
using System;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace CANguru.GUI.Forms
{
    public partial class PowerDecoderSettingsForm : Form, IDecoderSettingsForm
    {
        private readonly PowerDecoder decoder;

        public PowerDecoderSettingsForm(byte assignedId)
        {
            this.decoder = new PowerDecoder { AssignedId = assignedId };
            InitializeComponent();
            CurrentSettingsFormManager.Register(this);
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CurrentSettingsFormManager.Unregister(this);
            base.OnFormClosed(e);
        }

        private void PowerDecoderSettingsForm_Load(object sender, EventArgs e)
        {
            LoadFromDecoder();
        }


        public ushort GetThreshold(ushort defaultValue)
        {
            return DecoderProtocol.ReadThreshold(decoder.AssignedId, defaultValue);
        }

        // Grenzwert setzen (Little Endian); der Decoder antwortet mit dem tatsächlich
        // gesetzten Wert (begrenzt auf den Messbereich)
        public async void SetThreshold(ushort thr)
        {
            var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_POWER,
                (byte)ProtocolConstants.CG2_POWER_SET_THRESHOLD, decoder.AssignedId,
                new Cg2Writer().U16(thr).ToArray());
            if (r == null || r.IsError || r.Payload.Length < 2)
            {
                Form1.Instance?.Log($"Stromdecoder {decoder.AssignedId}: Grenzwert nicht gespeichert.");
                return;
            }
            LoadSettingsIntoUI(r.Payload);
        }
        private void LoadSettingsIntoUI(params byte[] payload)
        {
            if (payload == null || payload.Length < ProtocolConstants.EXP_LNG_POWER_SETTINGS)
                return;
            ushort thr = new Cg2Reader(payload).U16();

            this.Invoke(() =>
            {
                panel.numThreshold.Value = thr;
            });
        }

        private void LoadFromDecoder()
        {
            // Beispielwert
            panel.numThreshold.Value = 500;
            Task.Run(() =>
            {
                ushort thr = GetThreshold(2000);
                this.Invoke(() =>
                {
                    panel.numThreshold.Value = thr;
                });
            });

        }

        private void SaveToDecoder()
        {
            int threshold = (int)panel.numThreshold.Value;

            SetThreshold((ushort)threshold);
        }

        private void btnOK_Click(object sender, EventArgs e)
        {
            SaveToDecoder();
            this.DialogResult = DialogResult.None; //   .Cancel;
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        public void OnResetAck(byte assignedId, byte decoderType)
        {
            // NICHT direkt RequestAndReloadSettings aufrufen
            // sondern asynchron / auf UI-Thread verschieben
            this.BeginInvoke(new Action(() =>
            {
                var payload = DecoderProtocol.RequestAndReloadSettings(assignedId, decoderType);

                if (payload != null)
                    LoadSettingsIntoUI(payload);
                MessageBox.Show(
    "Die Standardeinstellungen wurden zurückgesetzt.",
    "Werkseinstellungen",
    MessageBoxButtons.OK,
    MessageBoxIcon.Warning
);

            }));
        }

        private void btnResetToDefaults_Click(object sender, EventArgs e)
        {
            byte id = decoder.AssignedId;
            Task.Run(() =>
            {
                if (DecoderProtocol.FactoryReset(id))
                    OnResetAck(id, (byte)ProtocolConstants.DEVTYPE_POWER);
                else
                    BeginInvoke(new Action(() => MessageBox.Show(this,
                        "Der Decoder hat die Werkseinstellungen nicht bestätigt.",
                        "Werkseinstellungen", MessageBoxButtons.OK, MessageBoxIcon.Warning)));
            });
        }
    }
}
