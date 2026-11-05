using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace FirmwareFlasher
{
    // Flashes ESP32 / ESP32-C3 boards (CANguru decoders and bridge) with esptool.
    //
    // Two kinds of files are accepted:
    //  - complete image (*-komplett.bin from alles-bauen.ps1): bootloader, partition
    //    table, OTA selection and application in one file, written at 0x0. Always works,
    //    also on an erased chip.
    //  - application only (firmware.bin / decoder-xxx.bin): written at 0x10000. Afterwards
    //    the OTA selection (otadata) is erased, otherwise a board that last ran from the
    //    second app slot after an OTA update would keep starting the old firmware.
    public partial class Form1 : Form
    {
        private const string Baud = "460800";
        private const int AppOffset = 0x10000;
        private const int OtadataOffset = 0xE000;
        private const int OtadataSize = 0x2000;
        private const int PartitionTableOffset = 0x8000;

        // Ports whose flash was erased: an application alone would not start there
        private readonly HashSet<string> erasedPorts = new();
        private readonly System.Windows.Forms.Timer portTimer = new() { Interval = 800 };
        private bool busy;
        private int progressLineStart = -1; // start of the last progress line in the log

        public Form1()
        {
            InitializeComponent();

            // Port list: on opening the list, on the button and automatically when a
            // USB device is plugged in or removed (WM_DEVICECHANGE, slightly delayed)
            cmbPorts.DropDown += (s, e) => LoadPorts();
            portTimer.Tick += (s, e) =>
            {
                portTimer.Stop();
                LoadPorts();
            };
            FormClosing += Form1_FormClosing;

            LoadPorts();
            Log("Komplettes Image (*-komplett.bin) wählen – das funktioniert immer.");
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DEVICECHANGE = 0x0219;
            if (m.Msg == WM_DEVICECHANGE)
            {
                portTimer.Stop();
                portTimer.Start();
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------
        // Ports
        // ------------------------------------------------------------

        private void LoadPorts()
        {
            string? selected = cmbPorts.SelectedItem as string;
            var before = cmbPorts.Items.Cast<string>().ToHashSet();

            var ports = SerialPort.GetPortNames()
                .Distinct()
                .OrderBy(p => int.TryParse(p.AsSpan(3), out int n) ? n : int.MaxValue)
                .ThenBy(p => p)
                .ToArray();

            cmbPorts.BeginUpdate();
            cmbPorts.Items.Clear();
            cmbPorts.Items.AddRange(ports);
            cmbPorts.EndUpdate();

            // A newly plugged board is most likely the one to flash
            var added = ports.Where(p => !before.Contains(p)).ToList();
            if (before.Count > 0 && added.Count == 1)
                selected = added[0];

            if (selected != null && ports.Contains(selected))
                cmbPorts.SelectedItem = selected;
            else if (ports.Length > 0)
                cmbPorts.SelectedIndex = 0;

            if (before.Count > 0)
            {
                foreach (string p in added)
                    Log($"Neuer Anschluss: {p}");
                foreach (string p in before.Where(p => !ports.Contains(p)))
                    Log($"Anschluss entfernt: {p}");
            }
        }

        private void btnRefreshPorts_Click(object? sender, EventArgs e)
        {
            LoadPorts();
            Log(cmbPorts.Items.Count == 0
                ? "Kein Anschluss gefunden."
                : "Anschlüsse: " + string.Join(", ", cmbPorts.Items.Cast<string>()));
        }

        // ------------------------------------------------------------
        // Buttons
        // ------------------------------------------------------------

        private void btnBrowse_Click(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Komplettes Image (*-komplett.bin)|*-komplett.bin|Firmware (*.bin)|*.bin",
                InitialDirectory = FindBuildDirectory() ?? ""
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                txtFirmware.Text = dlg.FileName;
        }

        private async void btnFlash_Click(object? sender, EventArgs e)
        {
            string file = txtFirmware.Text.Trim();
            if (!File.Exists(file))
            {
                Log("Bitte eine gültige Firmware-Datei auswählen.");
                return;
            }
            if (cmbPorts.SelectedItem is not string port)
            {
                Log("Bitte einen Anschluss auswählen.");
                return;
            }

            SetBusy(true);
            try
            {
                bool ok = await FlashAsync(port, file);
                Log(ok ? "==> Flashen erfolgreich. Der Decoder startet neu." : "==> Flashen FEHLGESCHLAGEN.");
            }
            catch (Exception ex)
            {
                Log("==> Fehler: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void btnEraseFlash_Click(object? sender, EventArgs e)
        {
            if (cmbPorts.SelectedItem is not string port)
            {
                Log("Bitte einen Anschluss auswählen.");
                return;
            }
            if (MessageBox.Show(this,
                    $"Den gesamten Flash-Speicher an {port} löschen?\r\n\r\n" +
                    "Dabei gehen Firmware, Bootloader und alle Einstellungen des Decoders verloren. " +
                    "Danach muss ein komplettes Image (*-komplett.bin) geflasht werden.",
                    "Flash löschen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            SetBusy(true);
            try
            {
                string? chip = await DetectChipAsync(port);
                if (chip == null)
                    return;
                Log($"Lösche den Flash-Speicher ({chip}) ...");
                int exit = await RunEsptoolAsync($"--chip {chip} --port {port} --baud {Baud} erase_flash");
                if (exit == 0)
                {
                    erasedPorts.Add(port);
                    Log("==> Flash gelöscht. Jetzt ein komplettes Image (*-komplett.bin) flashen.");
                }
                else
                    Log("==> Löschen FEHLGESCHLAGEN.");
            }
            catch (Exception ex)
            {
                Log("==> Fehler: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!busy)
                return;
            // Abbrechen mitten im Schreiben hinterließe einen Decoder, der nicht startet
            MessageBox.Show(this, "Bitte warten, bis der Vorgang beendet ist.", "Flasher",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
        }

        private void SetBusy(bool value)
        {
            busy = value;
            btnFlash.Enabled = !value;
            btnEraseFlash.Enabled = !value;
            btnBrowse.Enabled = !value;
            btnRefreshPorts.Enabled = !value;
            cmbPorts.Enabled = !value;
            txtFirmware.Enabled = !value;
            UseWaitCursor = value;
        }

        // ------------------------------------------------------------
        // Flashing
        // ------------------------------------------------------------

        private async Task<bool> FlashAsync(string port, string file)
        {
            byte[] data = await File.ReadAllBytesAsync(file);
            FirmwareImage? image = FirmwareImage.Analyse(data, out string error);
            if (image == null)
            {
                Log("Datei abgelehnt: " + error);
                return false;
            }
            Log($"Datei: {Path.GetFileName(file)} ({data.Length:N0} Bytes), " +
                (image.Complete ? "komplettes Image" : "nur Programm") +
                (image.Chip != null ? $" für {image.Chip}" : ""));

            string? chip = await DetectChipAsync(port);
            if (chip == null)
                return false;

            if (image.Chip != null && image.Chip != chip)
            {
                Log($"Die Datei ist für {image.Chip}, angeschlossen ist aber ein {chip}. Abbruch.");
                return false;
            }

            if (image.Complete)
            {
                Log("Schreibe komplettes Image an 0x0 ...");
                int exit = await RunEsptoolAsync($"--chip {chip} --port {port} --baud {Baud} write_flash -z 0x0 \"{file}\"");
                if (exit != 0)
                    return false;
                erasedPorts.Remove(port);
                return true;
            }

            if (erasedPorts.Contains(port))
            {
                Log("Der Flash-Speicher wurde gelöscht: ohne Bootloader startet ein reines Programm nicht. " +
                    "Bitte das komplette Image (*-komplett.bin) wählen.");
                return false;
            }

            Log($"Schreibe Programm an 0x{AppOffset:X} ...");
            int writeExit = await RunEsptoolAsync(
                $"--chip {chip} --port {port} --baud {Baud} write_flash -z 0x{AppOffset:X} \"{file}\"");
            if (writeExit != 0)
                return false;

            // Erst nach erfolgreichem Schreiben: sonst startet der Decoder weiter das alte Programm
            Log("Setze die OTA-Auswahl zurück, damit das neue Programm startet ...");
            int eraseExit = await RunEsptoolAsync(
                $"--chip {chip} --port {port} --baud {Baud} erase_region 0x{OtadataOffset:X} 0x{OtadataSize:X}");
            if (eraseExit != 0)
            {
                Log("Die OTA-Auswahl konnte nicht zurückgesetzt werden. War der Decoder zuletzt per OTA " +
                    "aktualisiert, startet er womöglich noch das alte Programm.");
                return false;
            }
            return true;
        }

        private async Task<string?> DetectChipAsync(string port)
        {
            Log($"Erkenne den Chip an {port} ...");
            var output = new StringBuilder();
            int exit = await RunEsptoolAsync($"--port {port} chip_id", output, logOutput: false);

            // esptool 4: "Chip is ESP32-C3 (QFN32) ...", esptool 5: "Chip type: ESP32-C3 ..."
            Match m = Regex.Match(output.ToString(), @"Chip (?:is|type:)\s*(ESP32(?:-[A-Z0-9]+)?)", RegexOptions.IgnoreCase);
            if (exit != 0 || !m.Success)
            {
                Log(output.ToString().TrimEnd());
                Log("Chip nicht erkannt. Ist der Decoder angeschlossen und der richtige Anschluss gewählt? " +
                    "Notfalls beim Einstecken die BOOT-Taste gedrückt halten.");
                return null;
            }
            // "ESP32-C3", "ESP32-S3" ... sind eigene Familien; "ESP32-D0WD-V3" u. ä. ist ein klassischer ESP32
            string name = m.Groups[1].Value.ToUpperInvariant();
            string chip = name switch
            {
                var n when n.StartsWith("ESP32-C3") => "esp32c3",
                var n when n.StartsWith("ESP32-C6") => "esp32c6",
                var n when n.StartsWith("ESP32-S2") => "esp32s2",
                var n when n.StartsWith("ESP32-S3") => "esp32s3",
                var n when n.StartsWith("ESP32-H2") => "esp32h2",
                _ => "esp32"
            };
            Log($"Gefundener Chip: {name} ({chip})");
            return chip;
        }

        // ------------------------------------------------------------
        // esptool
        // ------------------------------------------------------------

        // esptool.exe next to the flasher or on the PATH; otherwise esptool.py from PlatformIO
        private static (string fileName, string prefix)? FindEsptool()
        {
            string local = Path.Combine(AppContext.BaseDirectory, "esptool.exe");
            if (File.Exists(local))
                return (local, "");

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "esptool.exe");
                    if (File.Exists(candidate))
                        return (candidate, "");
                }
                catch (ArgumentException)
                {
                    // ungültiger Eintrag im PATH
                }
            }

            string pio = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".platformio");
            string python = Path.Combine(pio, "penv", "Scripts", "python.exe");
            string script = Path.Combine(pio, "packages", "tool-esptoolpy", "esptool.py");
            if (File.Exists(python) && File.Exists(script))
                return (python, $"\"{script}\" ");

            return null;
        }

        // Runs esptool; every output line goes to the log (progress lines overwrite each other)
        private async Task<int> RunEsptoolAsync(string args, StringBuilder? capture = null, bool logOutput = true)
        {
            var tool = FindEsptool();
            if (tool == null)
            {
                Log("esptool nicht gefunden. Bitte esptool.exe neben den Flasher legen, in den PATH " +
                    "aufnehmen oder VS Code mit PlatformIO installieren.");
                return -1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = tool.Value.fileName,
                Arguments = tool.Value.prefix + args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = new Process { StartInfo = psi };
            void OnLine(string? line)
            {
                if (line == null)
                    return;
                if (capture != null)
                    lock (capture)
                        capture.AppendLine(line);
                if (logOutput && line.Length > 0)
                    Log(line, IsProgress(line));
            }
            p.OutputDataReceived += (s, e) => OnLine(e.Data);
            p.ErrorDataReceived += (s, e) => OnLine(e.Data);

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return p.ExitCode;
        }

        private static bool IsProgress(string line) =>
            line.StartsWith("Writing at", StringComparison.Ordinal) ||
            line.StartsWith("Reading at", StringComparison.Ordinal);

        // ------------------------------------------------------------
        // Log (thread-safe: esptool output arrives on worker threads)
        // ------------------------------------------------------------

        private void Log(string msg, bool progress = false)
        {
            if (IsDisposed)
                return;
            if (InvokeRequired)
            {
                BeginInvoke(() => Log(msg, progress));
                return;
            }

            // Fortschrittszeilen überschreiben einander, statt das Fenster zu fluten
            if (progress && progressLineStart >= 0)
            {
                txtLog.Select(progressLineStart, txtLog.TextLength - progressLineStart);
                txtLog.SelectedText = "";
            }
            progressLineStart = progress ? txtLog.TextLength : -1;
            txtLog.AppendText(msg + Environment.NewLine);
        }

        // _build folder of the CANguru repository, if the flasher runs from inside it
        private static string? FindBuildDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "_build");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }

    // What a .bin file contains, read from the ESP image headers
    internal sealed class FirmwareImage
    {
        public bool Complete { get; private init; }
        public string? Chip { get; private init; }

        private const byte ImageMagic = 0xE9;

        public static FirmwareImage? Analyse(byte[] b, out string error)
        {
            error = "";

            // Komplettes Image: Partitionstabelle (0xAA 0x50) an 0x8000, Bootloader an 0x0 (C3/S3) oder 0x1000 (ESP32/S2)
            if (b.Length > 0x10010 && b[0x8000] == 0xAA && b[0x8001] == 0x50)
            {
                int bootloader = b[0] == ImageMagic ? 0 : (b[0x1000] == ImageMagic ? 0x1000 : -1);
                if (bootloader < 0 || b[0x10000] != ImageMagic)
                {
                    error = "Das Image enthält keinen gültigen Bootloader oder kein Programm an 0x10000.";
                    return null;
                }
                return new FirmwareImage { Complete = true, Chip = ChipName(b, bootloader) };
            }

            if (b.Length >= 2 && b[0] == 0xAA && b[1] == 0x50)
            {
                error = "Das ist eine Partitionstabelle (partitions.bin), keine Firmware.";
                return null;
            }
            if (b.Length < 24 || b[0] != ImageMagic)
            {
                error = "Das ist keine ESP32-Firmware (erstes Byte muss 0xE9 sein).";
                return null;
            }
            if (b.Length < 0x10000)
            {
                error = "Die Datei ist zu klein für ein Programm – vermutlich der Bootloader (bootloader.bin). " +
                        "Bitte firmware.bin oder das komplette Image wählen.";
                return null;
            }
            return new FirmwareImage { Complete = false, Chip = ChipName(b, 0) };
        }

        // Chip-ID im erweiterten Image-Kopf (Byte 12/13)
        private static string? ChipName(byte[] b, int offset)
        {
            int id = b[offset + 12] | (b[offset + 13] << 8);
            return id switch
            {
                0 => "esp32",
                2 => "esp32s2",
                5 => "esp32c3",
                9 => "esp32s3",
                _ => null
            };
        }
    }
}
