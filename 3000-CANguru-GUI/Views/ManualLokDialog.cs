using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using CANguru.Models;

namespace CANguru.Views
{
    public partial class ManualLokDialog : Form
    {
        public CANguruLok ResultLok { get; private set; }

        // detects the address of the locomotive on the programming track (track box), may be null
        private readonly Func<Task<CANguru.Controllers.CvResult>>? detectAddress;
        private readonly Button buttonDetect = new()
        {
            Text = "Digital-Lok-Adresse erkennen",
            Location = new Point(250, 50),
            Size = new Size(180, 25)
        };

        public ManualLokDialog(Func<Task<CANguru.Controllers.CvResult>>? detectAddress = null)
        {
            InitializeComponent();
            comboDecoderType.Items.Add("MM1");
            comboDecoderType.Items.Add("MM2");
            comboDecoderType.SelectedIndex = 1; // Standard: MM2

            this.detectAddress = detectAddress;
            ClientSize = new Size(450, ClientSize.Height);
            buttonDetect.Enabled = detectAddress != null;
            buttonDetect.Click += async (_, _) => await DetectAddressAsync();
            new ToolTip().SetToolTip(buttonDetect,
                "Lok allein auf das Programmiergleis stellen. Die Gleisbox sucht die Motorola-Adresse (dauert einige Sekunden).");
            Controls.Add(buttonDetect);
        }

        private async Task DetectAddressAsync()
        {
            buttonDetect.Enabled = false;
            buttonOK.Enabled = false;
            string text = buttonDetect.Text;
            buttonDetect.Text = "Suche Adresse …";
            try
            {
                var r = await detectAddress!();
                if (r.Ok && r.Value >= numericSID.Minimum && r.Value <= numericSID.Maximum)
                {
                    numericSID.Value = r.Value;
                    if (string.IsNullOrWhiteSpace(textName.Text))
                        textName.Text = $"MM2-{r.Value}";
                }
                else
                    MessageBox.Show(this, r.Ok ? $"Ungültige Adresse {r.Value}." : r.Message, "Digital-Lok-Adresse erkennen",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "Digital-Lok-Adresse erkennen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                buttonDetect.Text = text;
                buttonDetect.Enabled = true;
                buttonOK.Enabled = true;
            }
        }

        private void buttonOK_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(textName.Text))
            {
                MessageBox.Show("Bitte einen Namen eingeben.");
                return;
            }

            if (comboDecoderType.SelectedIndex < 0)
            {
                MessageBox.Show("Bitte einen Decoder-Typ auswählen.");
                return;
            }

            byte sid = (byte)numericSID.Value;
            string decoder = comboDecoderType.SelectedItem.ToString();

            // künstliche UID erzeugen
            uint uid = MakeManualUid(sid, decoder);

            // MfxUID = nur die Adresse
            byte[] mfxUid = new byte[] { sid };

            ResultLok = new CANguruLok
            {
                Name = textName.Text.Trim(),
                MfxSID = sid,
                DecoderType = decoder,
                IsManual = true,
                UID = uid,
                MfxUID = mfxUid
            };

            DialogResult = DialogResult.OK;
            Close();
        }

        // artificial UID of a manually entered locomotive (also used when its address changes)
        public static uint MakeManualUid(byte sid, string decoderType)
        {
            byte proto = decoderType == "MM1" ? (byte)0x01 : (byte)0x02;

            return (uint)(0xFF << 24 | proto << 16 | 0x00 << 8 | sid);
        }

        private void buttonCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
