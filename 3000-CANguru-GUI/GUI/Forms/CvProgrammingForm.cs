using CANguru.Controllers;
using CANguru.Models;
using System.Drawing;
using System.Windows.Forms;

namespace CANguru.GUI.Forms
{
    // CV programming of locomotive decoders, functions as in CANguru-Server 3.02:
    //  - read / write a CV of the locomotive on the programming track
    //  - read / write the name of the selected mfx locomotive
    public sealed class CvProgrammingForm : Form
    {
        private readonly CvProgrammer programmer;
        private readonly CANguruLok? lok;

        // common CVs to choose from; any other CV number can be typed in
        private static readonly string[] CommonCvs =
        {
            "1 – Adresse",
            "2 – Anfahrspannung (Vmin)",
            "3 – Beschleunigung",
            "4 – Bremsverzögerung",
            "5 – Höchstgeschwindigkeit (Vmax)",
            "6 – Mittlere Geschwindigkeit",
            "7 – Versionsnummer (nur lesen)",
            "8 – Hersteller (8 schreiben = Werkseinstellung)",
            "17 – Lange Adresse, oberer Teil",
            "18 – Lange Adresse, unterer Teil",
            "19 – Traktionsadresse",
            "29 – Konfiguration",
            "63 – Lautstärke (Märklin-Sounddecoder)"
        };
        private readonly ComboBox cvAddress = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 140, DropDownWidth = 290, MaxDropDownItems = 14 };
        private readonly Button btnClose = new() { Text = "Schließen", Width = 80, DialogResult = DialogResult.Cancel };
        private readonly TextBox oldValue = new() { ReadOnly = true, Width = 60, TextAlign = HorizontalAlignment.Right };
        private readonly NumericUpDown newValue = new() { Minimum = 0, Maximum = 255, Width = 60 };
        private readonly Button btnRead = new() { Text = "Lesen", Width = 80 };
        private readonly Button btnWrite = new() { Text = "Schreiben", Width = 80 };
        private readonly Label result = new() { AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold) };

        private readonly TextBox lokName = new() { Width = 200, MaxLength = 16 };
        private readonly Button btnNameRead = new() { Text = "Lesen", Width = 80 };
        private readonly Button btnNameWrite = new() { Text = "Schreiben", Width = 80 };

        private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(10, 4, 10, 0) };

        public CvProgrammingForm(CvProgrammer programmer, CANguruLok? lok)
        {
            this.programmer = programmer;
            this.lok = lok;
            Text = "CV-Programmierung";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(470, 335);
            CancelButton = btnClose;

            // ---- CV lesen / schreiben ----
            var cvBox = new GroupBox { Text = "CV lesen / schreiben (Lok auf dem Programmiergleis)", Location = new Point(10, 10), Size = new Size(450, 120) };
            cvBox.Controls.Add(new Label { Text = "CV-Adresse", AutoSize = true, Location = new Point(12, 28) });
            cvAddress.Location = new Point(100, 25);
            cvAddress.Items.AddRange(CommonCvs);
            cvAddress.SelectedIndex = 0;
            cvBox.Controls.Add(cvAddress);
            cvBox.Controls.Add(new Label { Text = "Ergebnis", AutoSize = true, Location = new Point(250, 28) });
            result.Location = new Point(320, 28);
            cvBox.Controls.Add(result);

            cvBox.Controls.Add(new Label { Text = "Alter Wert", AutoSize = true, Location = new Point(12, 68) });
            btnRead.Location = new Point(100, 63);
            oldValue.Location = new Point(186, 64);
            cvBox.Controls.Add(btnRead);
            cvBox.Controls.Add(oldValue);

            cvBox.Controls.Add(new Label { Text = "Neuer Wert", AutoSize = true, Location = new Point(250, 68) });
            newValue.Location = new Point(320, 64);
            btnWrite.Location = new Point(320, 90);
            cvBox.Controls.Add(newValue);
            cvBox.Controls.Add(btnWrite);
            cvBox.Height = 125;

            // ---- Lokname (nur mfx-Loks) ----
            var nameBox = new GroupBox { Text = "Lokname (nur mfx-Loks)", Location = new Point(10, 145), Size = new Size(450, 115) };
            bool mfx = lok != null && !lok.IsManual;
            string who = lok == null ? "keine Lok gewählt (in der Lokliste auswählen)"
                       : mfx ? $"gewählte Lok: {lok.Name} (mfx, Adresse {lok.MfxSID})"
                       : $"gewählte Lok: {lok.Name} – keine mfx-Lok";
            nameBox.Controls.Add(new Label { Text = who, AutoSize = true, Location = new Point(12, 25), ForeColor = mfx ? SystemColors.ControlText : Color.DimGray });
            nameBox.Controls.Add(new Label { Text = "Lokname", AutoSize = true, Location = new Point(12, 58) });
            lokName.Location = new Point(100, 55);
            btnNameRead.Location = new Point(100, 82);
            btnNameWrite.Location = new Point(186, 82);
            nameBox.Controls.AddRange(new Control[] { lokName, btnNameRead, btnNameWrite });
            lokName.Enabled = btnNameRead.Enabled = btnNameWrite.Enabled = mfx;

            btnClose.Location = new Point(380, 270);

            Controls.Add(cvBox);
            Controls.Add(nameBox);
            Controls.Add(btnClose);
            Controls.Add(status);

            btnRead.Click += async (s, e) => await ReadCv();
            btnWrite.Click += async (s, e) => await WriteCv();
            btnNameRead.Click += async (s, e) => await ReadName();
            btnNameWrite.Click += async (s, e) => await WriteName();
        }

        // CV number from the box: the number at the start ("3 – Beschleunigung" or "3"); 0 = invalid
        private int SelectedCv()
        {
            var digits = new string(cvAddress.Text.Trim().TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int cv) && cv >= 1 && cv <= 1024)
                return cv;
            ShowResult(false, "Bitte eine CV-Nummer von 1 bis 1024 wählen oder eingeben.");
            return 0;
        }

        private async Task ReadCv()
        {
            int cv = SelectedCv();
            if (cv == 0)
                return;
            oldValue.Text = "";
            await Run($"Lese CV {cv} ...", async () =>
            {
                var r = await programmer.ReadCvAsync(cv);
                if (r.Ok)
                {
                    oldValue.Text = r.Value.ToString();
                    newValue.Value = r.Value;
                }
                ShowResult(r.Ok, r.Ok ? $"CV {cv} = {r.Value}" : r.Message);
            });
        }

        private async Task WriteCv()
        {
            int cv = SelectedCv();
            if (cv == 0)
                return;
            byte value = (byte)newValue.Value;
            await Run($"Schreibe CV {cv} = {value} ...", async () =>
            {
                var r = await programmer.WriteCvAsync(cv, value);
                if (r.Ok)
                    oldValue.Text = r.Value.ToString();
                ShowResult(r.Ok, r.Ok ? $"CV {cv} auf {r.Value} gesetzt" : r.Message);
            });
        }

        private async Task ReadName()
        {
            lokName.Text = "";
            await Run("Lese den Loknamen ...", async () =>
            {
                var (ok, name, message) = await programmer.ReadMfxNameAsync(lok!.MfxSID);
                lokName.Text = name;
                ShowResult(ok, ok ? $"Lokname: {name}" : message);
            });
        }

        private async Task WriteName()
        {
            string name = lokName.Text.Trim();
            if (name.Length == 0)
                return;
            await Run($"Schreibe den Loknamen „{name}“ ...", async () =>
            {
                var (ok, message) = await programmer.WriteMfxNameAsync(lok!.MfxSID, name);
                ShowResult(ok, ok ? $"Lokname „{name}“ geschrieben" : message);
            });
        }

        // one operation at a time; buttons locked meanwhile
        private async Task Run(string text, Func<Task> action)
        {
            SetBusy(true);
            result.Text = "";
            status.Text = text;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                ShowResult(false, ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ShowResult(bool ok, string text)
        {
            result.Text = ok ? "OK" : "Fehler";
            result.ForeColor = ok ? Color.DarkGreen : Color.Red;
            status.Text = text;
        }

        private void SetBusy(bool busy)
        {
            bool mfx = lok != null && !lok.IsManual;
            btnRead.Enabled = btnWrite.Enabled = cvAddress.Enabled = newValue.Enabled = btnClose.Enabled = !busy;
            btnNameRead.Enabled = btnNameWrite.Enabled = lokName.Enabled = !busy && mfx;
            UseWaitCursor = busy;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // do not leave an operation half done (track power would stay switched)
            if (programmer.Busy)
            {
                e.Cancel = true;
                status.Text = "Bitte warten, bis die Programmierung beendet ist.";
            }
            base.OnFormClosing(e);
        }
    }
}
