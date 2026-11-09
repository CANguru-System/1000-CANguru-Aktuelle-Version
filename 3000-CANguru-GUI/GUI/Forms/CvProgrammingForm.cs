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
        private readonly Button btnFunctions = new() { Text = "Funktionen lesen", Width = 120 };
        private readonly Func<CANguruLok, Task<string>>? readFunctions;

        // exploration of the mfx configuration (which properties does the decoder describe?)
        private const int ExploreStartIndex = 0, ExploreCount = 17; // index 0..16
        private readonly NumericUpDown exploreFrom = new() { Minimum = 0, Maximum = 1023, Value = 0, Width = 60 };
        private readonly NumericUpDown exploreTo = new() { Minimum = 0, Maximum = 1023, Value = 63, Width = 60 };
        private readonly Button btnExplore = new() { Text = "Auslesen", Width = 80 };
        private readonly Button btnExploreStop = new() { Text = "Abbrechen", Width = 80, Enabled = false };
        private CancellationTokenSource? exploreCancel;

        private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(10, 4, 10, 0) };

        // readFunctions: reads the functions of the locomotive into the locomotive list (Form1)
        public CvProgrammingForm(CvProgrammer programmer, CANguruLok? lok, Func<CANguruLok, Task<string>>? readFunctions = null)
        {
            this.programmer = programmer;
            this.lok = lok;
            this.readFunctions = readFunctions;
            Text = "CV-Programmierung";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(470, 450);
            CancelButton = btnClose;

            // ---- selected locomotive, on top ----
            bool mfx = lok != null && !lok.IsManual;
            string who = lok == null ? "Gewählte Lok: –"
                       : mfx ? $"Gewählte Lok: {lok.Name} (mfx, Adresse {lok.MfxSID})"
                       : $"Gewählte Lok: {lok.Name} ({lok.DecoderType}, Adresse {lok.MfxSID})";
            Controls.Add(new Label
            {
                Text = who,
                AutoSize = true,
                Location = new Point(12, 12),
                Font = new Font(Control.DefaultFont.FontFamily, 10f, FontStyle.Bold)
            });

            // ---- CV lesen / schreiben ----
            var cvBox = new GroupBox { Text = "CV lesen / schreiben (Lok auf dem Programmiergleis)", Location = new Point(10, 40), Size = new Size(450, 120) };
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
            var nameBox = new GroupBox { Text = "Lokname und Funktionen (nur mfx-Loks)", Location = new Point(10, 175), Size = new Size(450, 110) };
            if (!mfx)
                nameBox.Controls.Add(new Label { Text = "Die gewählte Lok ist keine mfx-Lok.", AutoSize = true, Location = new Point(12, 22), ForeColor = Color.DimGray });
            nameBox.Controls.Add(new Label { Text = "Lokname", AutoSize = true, Location = new Point(12, 48) });
            lokName.Location = new Point(100, 45);
            btnNameRead.Location = new Point(100, 76);
            btnNameWrite.Location = new Point(186, 76);
            btnFunctions.Location = new Point(320, 76);
            nameBox.Controls.AddRange(new Control[] { lokName, btnNameRead, btnNameWrite, btnFunctions });
            new ToolTip().SetToolTip(btnFunctions, "Liest die Funktionen (F0, F1 …) aus dem Decoder und übernimmt sie in die Lokliste und in lokomotive.cs2.");
            lokName.Enabled = btnNameRead.Enabled = btnNameWrite.Enabled = btnFunctions.Enabled = mfx;

            // ---- mfx-Konfiguration erkunden ----
            var exploreBox = new GroupBox { Text = "mfx-Konfiguration erkunden (nur mfx-Loks)", Location = new Point(10, 295), Size = new Size(450, 85) };
            exploreBox.Controls.Add(new Label { Text = "CV von", AutoSize = true, Location = new Point(12, 28) });
            exploreFrom.Location = new Point(70, 25);
            exploreBox.Controls.Add(new Label { Text = "bis", AutoSize = true, Location = new Point(140, 28) });
            exploreTo.Location = new Point(170, 25);
            btnExplore.Location = new Point(260, 23);
            btnExploreStop.Location = new Point(350, 23);
            exploreBox.Controls.AddRange(new Control[] { exploreFrom, exploreTo, btnExplore, btnExploreStop });
            exploreBox.Controls.Add(new Label
            {
                Text = "Liest alles, was die Lok beschreibt, in eine Textdatei (dauert 1–2 Minuten).",
                AutoSize = true, Location = new Point(12, 58), ForeColor = Color.DimGray
            });
            exploreFrom.Enabled = exploreTo.Enabled = btnExplore.Enabled = mfx;
            Controls.Add(exploreBox);

            btnClose.Location = new Point(380, 388);

            Controls.Add(cvBox);
            Controls.Add(nameBox);
            Controls.Add(btnClose);
            Controls.Add(status);

            btnRead.Click += async (s, e) => await ReadCv();
            btnWrite.Click += async (s, e) => await WriteCv();
            btnNameRead.Click += async (s, e) => await ReadName();
            btnNameWrite.Click += async (s, e) => await WriteName();
            btnExplore.Click += async (s, e) => await Explore();
            btnFunctions.Click += async (s, e) => await ReadFunctions();
            btnExploreStop.Click += (s, e) => exploreCancel?.Cancel();
        }

        private async Task ReadFunctions()
        {
            if (readFunctions == null)
                return;
            await Run("Lese die Funktionen ...", async () =>
            {
                string text = await readFunctions(lok!);
                ShowResult(text.StartsWith("Funktionen:"), text);
            });
        }

        // Reads the mfx configuration CV by CV and writes it to a text file, which is opened
        private async Task Explore()
        {
            int from = (int)exploreFrom.Value, to = (int)exploreTo.Value;
            if (to < from)
            {
                ShowResult(false, "„bis“ muss größer oder gleich „von“ sein.");
                return;
            }
            exploreCancel = new CancellationTokenSource();
            btnExploreStop.Enabled = true;
            await Run("Lese die mfx-Konfiguration ...", async () =>
            {
                var map = await programmer.ExploreMfxAsync(lok!.MfxSID, from, to, ExploreStartIndex, ExploreCount,
                    text => BeginInvoke(() => status.Text = text), exploreCancel.Token);
                string file = WriteExploreFile(map, from, to, exploreCancel.IsCancellationRequested);
                ShowResult(map.Count > 0, map.Count > 0 ? $"{map.Count} CVs mit Inhalt – gespeichert in {Path.GetFileName(file)}"
                                                        : "Die Lok hat auf keine Anfrage geantwortet (auf dem Programmiergleis?).");
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
                }
                catch
                {
                    // the file is there anyway
                }
            });
            btnExploreStop.Enabled = false;
            exploreCancel = null;
        }

        private string WriteExploreFile(SortedDictionary<int, SortedDictionary<int, int>> map, int from, int to, bool cancelled)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "mfx-Konfiguration");
            Directory.CreateDirectory(dir);
            string safe = string.Concat(lok!.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            string file = Path.Combine(dir, $"{(safe.Length > 0 ? safe : "Lok")} {DateTime.Now:yyyy-MM-dd HHmm}.txt");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"mfx-Konfiguration – Lok „{lok.Name}“ (mfx-Adresse {lok.MfxSID}, UID {lok.UID:X8})");
            sb.AppendLine($"Gelesen am {DateTime.Now:dd.MM.yyyy HH:mm}, CV {from}–{to}, je Index {ExploreStartIndex}–{ExploreStartIndex + ExploreCount - 1}" +
                          (cancelled ? " (abgebrochen)" : ""));
            sb.AppendLine("Je CV: Index:Wert (hexadezimal), \"??\" = Antwort ohne Wert; danach als Text.");
            sb.AppendLine();
            foreach (var (cv, values) in map)
            {
                var hex = string.Join(" ", values.Select(kv => $"{kv.Key}:{(kv.Value < 0 ? "??" : kv.Value.ToString("X2"))}"));
                var text = new string(values.Values.Select(v => v >= 0x20 && v < 0x7F ? (char)v : '.').ToArray());
                sb.AppendLine($"CV {cv,4}: {hex}");
                sb.AppendLine($"         Text: {text}");
            }
            if (map.Count == 0)
                sb.AppendLine("Keine Antworten.");
            File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
            return file;
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
            btnNameRead.Enabled = btnNameWrite.Enabled = lokName.Enabled = btnFunctions.Enabled = !busy && mfx && (readFunctions != null);
            btnExplore.Enabled = exploreFrom.Enabled = exploreTo.Enabled = !busy && mfx;
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
