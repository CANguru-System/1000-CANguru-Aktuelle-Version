using CANguru;
using CANguru.Controllers;
using CANguru.Controls;
using CANguru.GUI.Forms;
using CANguru.Models;
using CANguru.Networking;
using CANguru.Protocol;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Timers;
using System.Windows.Forms;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Application = System.Windows.Forms.Application;

namespace CANguru.Views
{
    public partial class Form1 : Form
    {
        #region Felder und globale Zustände

        // CAN-Frames / Protokoll
        private CANFrames CANFrames;
        private CANguruLokRegistry lokRegistry = new();
        private MfxParser mfxParser;
        private LokStorage lokstorage;
        private readonly DecoderRegistry decoders = new();
        private volatile ParameterForm? activeParameterForm; // offenes allgemeines Parameter-Fenster
        private DecoderListForm decoderListForm;
        private LokListForm lokListForm;

        // Verbindung zur Bridge, CAN-Monitor
        private readonly BridgeSession session = new();
        private readonly CanMonitor canMonitor = new();

        // Firmware-Update
        private readonly CANguru.OTA.OtaService otaService;
        private CancellationTokenSource _uploadCts;

        // lokomotive.cs2 -Upload
        private Cs2FileUploader uploader;

        // Alive-Überwachung
        private bool guiClosing = false;

        private int lastPower = -1;
        private int lastSwitch = -1;
        private int lastSignal = -1;
        private int lastFeedback = -1;
        private int lastLedSignal = -1;

        // CANguru-Steuerzustand
        private enum enum_canguruStatus
        {
            idle,
            startGleisbox,
            wait4LokListe,
            systemIsRunning
        }
        private enum_canguruStatus canguruStatus;

        // Einmal-Timer für verzögerten Start
        private System.Windows.Forms.Timer oneShotTimer = new System.Windows.Forms.Timer();

        //
        public PowerDecoderController PowerCtrl = new PowerDecoderController();
        #endregion

        #region Konstruktor und Form-Initialisierung
        //
        public static Form1 Instance { get; private set; }
        // Für Drag-Move
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;
        private VerticalDualProgressBar dualBar;
        private ToggleStopGoButton btnStopGo;

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        // Einmal-Timer für Start der CANguru-Loop
        private void InitOneShotTimer()
        {
            oneShotTimer.Interval = 1000;
            oneShotTimer.Tick += OneShotTimer_Tick;
            oneShotTimer.Start();
        }
        private void OneShotTimer_Tick(object sender, EventArgs e)
        {
            oneShotTimer.Stop();
            oneShotTimer.Dispose();
            StartCANguruLoop();
        }

        private void InitDualBar()
        {
            dualBar = new VerticalDualProgressBar();
            dualBar.Visible = false;
            dualBar.CurrentMaximum = 100;
            dualBar.TotalMaximum = 100;
            dualBar.CurrentValue = 0;
            dualBar.TotalValue = 0;
            dualBar.CurrentColor = Color.LimeGreen;
            dualBar.TotalColor = Color.DodgerBlue;
            dualBar.DividerColor = Color.Black;
            dualBar.TextColor = Color.Black;

            // rechte Spalte unter dem Zähler "LED-Signale" (endet bei 493), über "Exit" (ab 644)
            dualBar.Width = 100;
            dualBar.Height = 132;
            dualBar.Left = 500;
            dualBar.Top = 502;

            this.Controls.Add(dualBar);
        }
        private void InitStopGoButton()
        {
            btnStopGo = new ToggleStopGoButton();
            btnStopGo.Width = 75;
            btnStopGo.Height = 40;
            btnStopGo.Left = 513;
            btnStopGo.Top = 560;

            // Aktionen verbinden
            btnStopGo.StopClicked += BtnStopGo_StopClicked;
            btnStopGo.GoClicked += BtnStopGo_GoClicked;

            this.Controls.Add(btnStopGo);
            Log("Gleisspannung ausgeschaltet.");
            btnStopGo.SetMode(false);
        }

        public Form1()
        {
            InitializeComponent();
            InitUploader();
            InitOneShotTimer();
            CANFrames = new CANFrames();
            mfxParser = new MfxParser(lokRegistry, CANFrames);
            lokListForm = new LokListForm();
            pictureBox1.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap();
            GuiLogger.MessageLogged += OnGuiLog;
            // Parser-Callbacks
            mfxParser.LogMessage += OnParserLog;
            mfxParser.incnextLocid += OnincnextLocid;
            mfxParser.LokCompleted += OnLokCompleted;
            mfxParser.SendFrameRequested += OnSendFrameRequested;
            mfxParser.FindKnownSid = FindKnownMfxSid;

            generateHash();

            canguruStatus = enum_canguruStatus.idle;

            // UI-Initialisierung für UpDowns
            UpDnnewSubscriptionCounter.Minimum = 1;
            UpDnnewSubscriptionCounter.Maximum = 255;
            CANFrames.newSubscriptionCounter = Properties.Settings.Default.newSubscriptionCounter;
            UpDnnewSubscriptionCounter.Value = CANFrames.newSubscriptionCounter;

            UpDnnextLokAddress.Minimum = 1;
            UpDnnextLokAddress.Maximum = 255;
            CANFrames.nextLokAddress = Properties.Settings.Default.nextLokAddress;
            UpDnnextLokAddress.Value = CANFrames.nextLokAddress;

            STOPP_Timer.Enabled = false;

            // Storage initialisieren
            lokstorage = new LokStorage("mfx_loks.json");
            ApplyNextMfxSIDToUI();

            // Fenster / Aussehen

            // Fenster ohne Rahmen
            this.FormBorderStyle = FormBorderStyle.None;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Schatten aktivieren
            this.Padding = new Padding(1);

            radioSingle.Checked = true;

            UpdateLokList();

            // Power
            PowerCtrl.ValuesUpdated += UpdatePowerUI;
            PowerCtrl.ShutdownTriggered += PowerShutdownUI;
            //
            Instance = this;

            // ListView Decoder-Liste
            decoderListForm = new DecoderListForm(decoders);
            // Firmware-Update: Fortschritt der Übertragung zur Bridge
            otaService = new CANguru.OTA.OtaService(uploadPercent: percent => UiInvoke(() =>
            {
                dualBar.CurrentValue = percent;
                dualBar.CurrentText = $"{percent}%";
                dualBar.Invalidate();
            }));
            InitOtaService();

            // Verbindung zur Bridge (Start mit "Verbinden")
            InitBridgeSession();
        }
        // Schatten hinzufügen
        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        // Abgerundete Ecken
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            int radius = 12;
            var path = new System.Drawing.Drawing2D.GraphicsPath();

            path.AddArc(0, 0, radius, radius, 180, 90);
            path.AddArc(this.Width - radius, 0, radius, radius, 270, 90);
            path.AddArc(this.Width - radius, this.Height - radius, radius, radius, 0, 90);
            path.AddArc(0, this.Height - radius, radius, radius, 90, 90);
            path.CloseAllFigures();

            this.Region = new Region(path);
        }

        // Drag-Move für Header
        private void headerPanel_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
        }

        // Schließen-Button
        private void btnClose_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        // Minimieren-Button
        private void btnMinimize_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
        }
        private void setProgressBar()
        {
            dualBar.CurrentMaximum = 100;
            dualBar.TotalMaximum = 100;

            dualBar.CurrentValue = 0;
            dualBar.TotalValue = 0;

            dualBar.CurrentColor = Color.LimeGreen;
            dualBar.TotalColor = Color.DodgerBlue;
            dualBar.DividerColor = Color.Black;
            dualBar.TextColor = Color.Black;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // ListBox-Loks initial mit Header + Loks füllen
            RefreshLokListBox();
            InitDualBar();
            setProgressBar();
            InitStopGoButton();
            canTimer.Start();
            canTimer.Enabled = true;
            // Optional: OwnerDraw für Decoder aktivieren
            // listBoxDecoder.DrawItem += ListBoxDecoder_DrawItem;

            // Rechtsklick auf einen Decoder: allgemeine Parameter, entfernen
            var menu = new ContextMenuStrip();
            menu.Items.Add("Parameter …", null, DecoderParameter_Click);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Decoder entfernen", null, DecoderEntfernen_Click);
            menu.Opening += (s, e) => e.Cancel = listBoxDecoder.SelectedItem is not CANguruDecoder;
            listBoxDecoder.ContextMenuStrip = menu;
            listBoxDecoder.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int i = listBoxDecoder.IndexFromPoint(e.Location);
                if (i >= 0) listBoxDecoder.SelectedIndex = i;
            };
        }

        #endregion

        #region Hilfsmethoden Basis (BitWrite, Hash, StartLoop, BridgeIP)

        // Allgemeines Bit-Setzen/Löschenprivate
        void OnGuiLog(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => tbMessages.AppendText(text + "\r\n")));
            }
            else
            {
                tbMessages.AppendText(text + "\r\n");
            }
        }

        private void BitWrite(ref uint value, int bit, int bitValue)
        {
            if (bitValue != 0)
                value |= (1u << bit);   // Bit setzen
            else
                value &= ~(1u << bit);  // Bit löschen
        }

        // Hash für die CAN-UID generieren
        private void generateHash()
        {
            uint uid = 0x45009195; // CAN-UID
            uid--;
            uint highbyte = 0x4500;
            uint lowbyte = 0x9195;
            uint hash = highbyte ^ lowbyte;

            BitWrite(ref hash, 7, 0);
            BitWrite(ref hash, 8, 1);
            BitWrite(ref hash, 9, 1);

            CANFrames.SetHash((byte)(hash >> 8), (byte)hash);
        }

        // Start der CANguru-Loop (Timer)
        private void StartCANguruLoop()
        {
            looptimer.Start();
        }

        // Prüfen, ob Bridge-IP gesetzt ist
        private bool BridgeIPIsNotSet()
        {
            return string.IsNullOrEmpty(ProtocolConfig.ipString);
        }

        #endregion

        #region UDP / Paketverarbeitung

        // Connection to the bridge: the session receives, searches the bridge and raises one
        // event per message kind (on the receive thread)
        private void InitBridgeSession()
        {
            session.Log += text => { if (!guiClosing) Log(text); };
            session.BridgeFound += HandleBridgeFound;
            session.TrackPowerChanged += HandleTrackPower;
            session.CanFrameReceived += canMonitor.Enqueue;
            session.DecoderAdded += HandleDecoderAdded;
            session.DecoderAlive += HandleDecoderAlive;
            session.AliveSummary += HandleAliveSummary;
            session.DecoderEvent += HandleDecoderEvent;
            session.OtaProgress += otaService.OnProgress;
            session.OtaResult += otaService.OnResult;
        }

        private int UpdateAliveField(System.Windows.Forms.TextBox box, int lastValue, int newValue)
        {
            if (InvokeRequired)
            {
                return (int)Invoke(new Func<int>(() => UpdateAliveField(box, lastValue, newValue)));
            }

            box.Text = newValue.ToString();

            if (lastValue != -1 && newValue < lastValue)
                box.BackColor = Color.Red;
            else
                box.BackColor = SystemColors.Window;

            return newValue;
        }

        // Bridge answered DISCOVER (also when no decoder is running yet)
        private async void HandleBridgeFound(BridgeInfo bridge)
        {
            Log($"Bridge gefunden: {bridge.Address}, Firmware {bridge.FwMajor}.{bridge.FwMinor}, Protokoll 2.{bridge.ProtoMinor}" +
                (bridge.Gleisbox ? ", Gleisbox erkannt" : ""));

            UiInvoke(() =>
            {
                txtBridgeIP.Text = ProtocolConfig.ipString;
                btnStopGo.Enabled = true;
                btnConnect.Text = "Verbunden";
            });

            // Lokliste zur Bridge, damit WDP sie abholen kann. Ohne Fortschrittsbalken:
            // die Datei ist klein und in Sekundenbruchteilen übertragen (der Balken blitzte
            // nur kurz auf und sah aus wie ein OTA-Update)
            bool ok = await uploader.StartUploadAsync("lokomotive.cs2");
            if (ok)
                Log("lokomotive.cs2 zur Bridge übertragen.");

            // Decoder zur Anmeldung auffordern
            var start = await BridgeLink.Instance.RequestAsync(
                (byte)ProtocolConstants.CG2_F_SESSION, (byte)ProtocolConstants.CG2_SESSION_START,
                (byte)ProtocolConstants.CG2_ID_BRIDGE, null);
            if (start == null)
                Log("Bridge bestätigt den Start nicht.");

            canguruStatus = enum_canguruStatus.startGleisbox;
        }

        // ALIVE of a decoder with its measured values
        private void HandleDecoderAlive(byte decoderId, byte status, int[] values)
        {
            if (!decoders.TryGetById(decoderId, out var dec))
                return;
            dec.LastSeen = DateTime.Now; // den Status berechnet aliveTimer aus LastSeen

            // offenes Parameter-Fenster zeigt die Messwerte laut Beschreibung an
            activeParameterForm?.UpdateValues(decoderId, values);

            // Stromdecoder: Messwerte = Strom je Kanal in mA, 0xFFFF = nicht angeschlossen
            if (dec.Type == DecoderType.Power && values.Length >= 2)
            {
                PowerCtrl.UpdateCurrent(0, values[0] == 0xFFFF ? -1 : values[0]);
                PowerCtrl.UpdateCurrent(1, values[1] == 0xFFFF ? -1 : values[1]);
            }
        }

        // SUMMARY: ready decoders per type
        private void HandleAliveSummary(IReadOnlyDictionary<byte, int> counts)
        {
            int Count(int type) => counts.TryGetValue((byte)type, out var n) ? n : 0;
            lastPower = UpdateAliveField(cntPower, lastPower, Count(ProtocolConstants.DEVTYPE_POWER));
            lastSwitch = UpdateAliveField(cntSwitch, lastSwitch, Count(ProtocolConstants.DEVTYPE_SWITCH));
            lastSignal = UpdateAliveField(cntSignal, lastSignal, Count(ProtocolConstants.DEVTYPE_SIGNAL));
            lastFeedback = UpdateAliveField(cntFeedback, lastFeedback, Count(ProtocolConstants.DEVTYPE_RM));
            lastLedSignal = UpdateAliveField(cntLedSignal, lastLedSignal, Count(ProtocolConstants.DEVTYPE_LEDSIGNAL));
        }

        // Event of a decoder (position, calibration, overcurrent ...)
        private void HandleDecoderEvent(byte decoderId, byte ev, byte[] data)
        {
            if (ev == ProtocolConstants.CG2_EV_OVERCURRENT)
                HandleOvercurrent(decoderId, data);
            activeParameterForm?.OnEvent(decoderId, ev, data);
        }

        // Gleisspannung an/aus (Sitzung TRACK_POWER von der Bridge, abgeleitet aus STOPP/GO)
        private void HandleTrackPower(bool on)
        {
            if (on)
            {
                Log("GO – Gleisspannung liegt an.");
                UiInvoke(() => btnStopGo.SetMode(true));
            }
            else
            {
                Log("STOPP – Gleisspannung ausgeschaltet.");
                UiInvoke(() => btnStopGo.SetMode(false));
            }
        }

        // Ereignis Überstrom eines Stromdecoders: [kanal][strom mA u16].
        // Den STOPP an die Gleisbox hat die Bridge schon gesendet.
        private void HandleOvercurrent(byte decoderId, byte[] data)
        {
            var r = new Cg2Reader(data);
            int channel = r.U8();
            ushort mA = r.U16();
            if (!r.Ok)
                return;
            PowerCtrl.UpdateCurrent(channel, mA);
            PowerCtrl.TriggerShutdown(channel);
            Log($"ÜBERSTROM auf Kanal {channel + 1} (Decoder {decoderId}): {mA} mA – Gleisspannung abgeschaltet.");
            // Knopf auf GO stellen, damit ein Klick wieder einschaltet
            UiInvoke(() => btnStopGo.SetMode(false));
        }

        #endregion
        #region Power-Anzeige
        /*
         *  | Aufgabe                      | Ort                            |
            | ---------------------------- | ------------------------------ |
            | Daten speichern              | **PowerDecoderModel**          |
            | Einstellungen repräsentieren | **PowerDecoderSettings**       |
            | Netzwerk senden/empfangen    | **GUI‑Code / Controller‑Code** |
            | UI aktualisieren             | **GUI‑Code**                   |
        */
        private void UpdatePowerUI()
        {
            if (guiClosing) return;
            this.BeginInvoke(new Action(() =>
            {
                ShowCurrent(labelCurrent0, panelCurrent0, PowerCtrl.Current0_mA);
                ShowCurrent(labelCurrent1, panelCurrent1, PowerCtrl.Current1_mA);
            }));

        }

        // Stromwert anzeigen; negativ = Kanal nicht angeschlossen
        private void ShowCurrent(Control label, Panel panel, int mA)
        {
            if (mA < 0)
            {
                label.Text = "–";
                panel.BackColor = SystemColors.Control;
                return;
            }
            label.Text = $"{mA} mA";
            panel.BackColor = mA > PowerCtrl.Threshold_mA ? Color.Red : Color.LightGreen;
        }

        private void PowerShutdownUI(int channel)
        {
            this.BeginInvoke(new Action(() =>
            {
                if (channel == 0)
                    panelCurrent0.BackColor = Color.Red;
                else
                    panelCurrent1.BackColor = Color.Red;
            }));
        }

        private void buttonSettings_Click(object sender, EventArgs e)
        {
            if (listBoxDecoder.SelectedItem is not CANguruDecoder decoder)
            {
                MessageBox.Show("Bitte einen Decoder auswählen.");
                return;
            }

            switch (decoder.Type)
            {
                case DecoderType.Switch:
                case DecoderType.Signal:
                case DecoderType.LedSignal:
                    // Zubehör-Decoder (Phase 3): Einstellungen und Kalibrierung über das
                    // Parameter-Fenster. Das Fenster mit Weichenbild kommt in Phase 5 zurück.
                    OpenParameterForm(decoder);
                    return;
                case DecoderType.Feedback:
                    // Rückmelder (Phase 3): Meldeadressen der 16 Kontakte im Parameter-Fenster
                    OpenParameterForm(decoder);
                    return;
                case DecoderType.Power:
                    // Stromdecoder (Phase 3): Abschaltgrenze und Ströme im Parameter-Fenster
                    OpenParameterForm(decoder);
                    return;
                default:
                    MessageBox.Show("Diesen Decoder gibt es nicht.");
                    return;
            }
        }

        #endregion Power-Anzeige

        #region Over The Air
        private void StopAllGuiTimers()
        {
            aliveTimer?.Stop();
            canTimer?.Stop();
            PINGtimer?.Stop();
        }
        private void StartAllGuiTimers()
        {
            aliveTimer?.Start();
            canTimer?.Start();
            PINGtimer?.Start();
        }

        private async void btnOtaUpdate_Click(object sender, EventArgs e)
        {
            // selection first: one decoder or all decoders of a type
            byte selector;
            bool single = radioSingle.Checked;
            if (single)
            {
                if (listBoxDecoder.SelectedItem is not CANguruDecoder decoder)
                {
                    MessageBox.Show("Bitte einen Decoder auswählen.");
                    return;
                }
                selector = (byte)decoder.AssignedId;
            }
            else
            {
                byte? type = null;
                if (radioType1.Checked) type = ProtocolConstants.DEVTYPE_SWITCH;
                else if (radioType2.Checked) type = ProtocolConstants.DEVTYPE_SIGNAL;
                else if (radioType3.Checked) type = ProtocolConstants.DEVTYPE_POWER;
                else if (radioType4.Checked) type = ProtocolConstants.DEVTYPE_RM;
                else if (radioType5.Checked) type = ProtocolConstants.DEVTYPE_LEDSIGNAL;
                if (type == null)
                {
                    MessageBox.Show("Bitte einen OTA-Modus auswählen.");
                    return;
                }
                selector = type.Value;
            }

            Log("Over The Air-Prozess gestartet.");

            // Firmware auswählen
            using var ofd = new OpenFileDialog();
            ofd.Filter = "Firmware (*.bin)|*.bin";
            if (ofd.ShowDialog() != DialogResult.OK)
                return;

            byte[] firmware = File.ReadAllBytes(ofd.FileName);
            Log($"Firmware-Datei wird geladen ({firmware.Length} Bytes).");

            // UI vorbereiten
            dualBar.Visible = true;
            dualBar.SingleBarMode = true;
            dualBar.CurrentValue = 0;
            StopAllGuiTimers();

            _uploadCts = new CancellationTokenSource();
            bool ok = await otaService.UploadAsync(firmware, _uploadCts.Token);
            StartAllGuiTimers();

            if (!ok)
            {
                Log("Firmware-Upload fehlgeschlagen.");
                dualBar.Visible = false;
                dualBar.SingleBarMode = false;
                return;
            }

            Log("Firmware erfolgreich an Bridge übertragen.");
            dualBar.Visible = true;
            dualBar.SingleBarMode = single;

            string? grund = await otaService.StartAsync(single, selector);
            if (grund != null)
            {
                dualBar.Visible = false;
                Log("OTA-Start nicht möglich: " + grund);
                MessageBox.Show(this, grund, "Firmware-Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Messages of the OTA service: progress bar and failure notice
        private void InitOtaService()
        {
            otaService.Log += text => Log(text);
            otaService.ProgressChanged += (percent, overall) => UiInvoke(() =>
            {
                dualBar.Visible = true;
                dualBar.CurrentValue = percent;
                dualBar.CurrentText = $"{percent}%";
                dualBar.TotalValue = overall;
                dualBar.TotalText = $"{overall}%";
                dualBar.Invalidate();
            });
            otaService.Finished += () => UiInvoke(() => dualBar.Visible = false);
            otaService.Failed += (decoderId, grund, oldFirmwareKept) => UiInvoke(() => MessageBox.Show(
                this,
                $"Das Update für Decoder {decoderId} ist fehlgeschlagen.\r\n\r\n{grund}\r\n\r\n" +
                (oldFirmwareKept
                    ? "Der Decoder arbeitet mit der bisherigen Firmware weiter."
                    : "Bitte prüfen, ob der Decoder in der Liste wieder erscheint; sonst kurz stromlos machen."),
                "Firmware-Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning));
        }

        #endregion over the air

        #region lokomotive.cs2 -Upload
        private void InitUploader()
        {
            uploader = new Cs2FileUploader();

            // Kein Fortschrittsbalken für die kleine Lokliste: der Balken gehört dem OTA-Update
            // und darf von hier nicht umgestellt werden

            uploader.UploadError += (msg) =>
            {
                Log("Fehler beim Upload lokomotive.cs2: " + msg);
                UiInvoke(() => MessageBox.Show(this, "Fehler beim Upload lokomotive.cs2: " + msg,
                    "lokomotive.cs2", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            };
        }
        #endregion

        #region Alive-Überwachung (canTimer/aliveTimer + Statusanzeige)

        // Alive-Timer (100 ms)
        private void canTimer_Tick(object sender, EventArgs e)
        {
            if (BridgeIPIsNotSet())
                return;

            // CAN-Frames sortieren + anzeigen
            ProcessPendingCanFrames();
        }

        // Alive-Statusanzeige
        private void UpdateUI()
        {
            var decoderSnapshot = decoders.All();

            foreach (var decoder in decoderSnapshot)
            {
                switch (decoder.Status)
                {
                    case DecoderStatus.Healthy:
                        lblBRIDGEStatus.Text = "ONLINE";
                        lblBRIDGEStatus.BackColor = Color.LimeGreen;
                        break;

                    case DecoderStatus.Weak:
                        lblBRIDGEStatus.Text = "WEAK";
                        lblBRIDGEStatus.BackColor = Color.Gold;
                        break;

                    case DecoderStatus.Lost:
                        lblBRIDGEStatus.Text = "OFFLINE";
                        lblBRIDGEStatus.BackColor = Color.Red;
                        break;
                }
            }
        }

        // GUI sendet Alive an Bridge
        private void aliveTimer_Tick(object sender, EventArgs e)
        {
            foreach (var decoder in decoders.All())
            {
                var delta = DateTime.Now - decoder.LastSeen;

                if (delta.TotalSeconds < 2)
                {
                    decoder.Status = DecoderStatus.Healthy;
                }
                else if (delta.TotalSeconds < 5)
                {
                    decoder.Status = DecoderStatus.Weak;
                }
                else
                {
                    decoder.Status = DecoderStatus.Lost;
                }
            }
            UpdateUI();
        }


        private void SendCANFrame2Bridge(byte[] canFrame)
        {
            // canFrame hat ein Zeichen mehr als CAN_FRAME_SIZE: "G"

            // Nur die ersten 13 Bytes senden, im CAN-Tunnel (Protokoll Version 2)
            byte[] frameToSend = new byte[ProtocolConstants.CAN_FRAME_SIZE];
            Array.Copy(canFrame, frameToSend, ProtocolConstants.CAN_FRAME_SIZE);

            BridgeLink.Instance.Send((byte)ProtocolConstants.CG2_F_CAN, (byte)ProtocolConstants.CG2_CAN_TO_TRACKBOX,
                                     (byte)ProtocolConstants.CG2_ID_BRIDGE, frameToSend);
            DisplayCanFrame(canFrame);
        }

        // Received CAN frames: sorted, shown and passed to the mfx parser
        private void ProcessPendingCanFrames()
        {
            foreach (var frame in canMonitor.TakeSorted())
            {
                DisplayCanFrame(frame);
                mfxParser.ProcessCanFrame(frame);
            }
        }

        // Show a CAN frame in the CAN list (and in the mfx list for registration frames)
        private void DisplayCanFrame(byte[] data)
        {
            string text = CanMonitor.Format(data);
            bool mfx = CanMonitor.IsMfxFrame(data);
            UiInvoke(() =>
            {
                listBoxCAN.AppendText(text);
                listBoxCAN.AppendText(Environment.NewLine);
                if (mfx)
                {
                    listBoxMFX.AppendText(text);
                    listBoxMFX.AppendText(Environment.NewLine);
                }
            });
        }

        #endregion

        #region Decoder-Handling (Liste + Zeichnen)

        private void HandleDecoderAdded(byte id, DecoderType type, byte[] mac,
                                        int fwMajor, int fwMinor)
        {
            CANguruDecoder decoder = type switch
            {
                DecoderType.Power => new PowerDecoder(),
                DecoderType.Switch => new StepperDecoder(),
                DecoderType.Signal => new StepperDecoder(),
                DecoderType.Feedback => new FeedbackDecoder(),
                DecoderType.LedSignal => new LedSignalDecoder(),
                _ => new CANguruDecoder()
            };

            decoder.AssignedId = id;
            decoder.Type = type;
            decoder.Mac = mac;
            decoder.Firmware = $"{fwMajor}.{fwMinor}";
            decoder.LastSeen = DateTime.Now;

            Log($"Decoder angemeldet: ID={id}, Typ={type}, Firmware {decoder.Firmware}");

            decoders.AddOrUpdate(decoder);
            RefreshDecoderListBox();

            // Einstellungen holen (Liste, beim Stromdecoder die Abschaltgrenze für die Anzeige)
            Task.Run(async () =>
            {
                await Task.Delay(300); // Decoder Zeit geben
                RefreshDecoderInfo(decoder);
            });
        }

        // Kontextmenü der Decoderliste: allgemeines Parameter-Fenster aus der Selbstbeschreibung
        private void DecoderParameter_Click(object? sender, EventArgs e)
        {
            if (listBoxDecoder.SelectedItem is CANguruDecoder decoder)
                OpenParameterForm(decoder);
        }

        private void OpenParameterForm(CANguruDecoder decoder)
        {
            string title = decoder.Type switch
            {
                DecoderType.Switch => "Weiche",
                DecoderType.Signal => "Formsignal",
                DecoderType.Power => "Stromdecoder",
                DecoderType.Feedback => "Rückmelder",
                DecoderType.LedSignal => "LED-Signale",
                _ => decoder.Type.ToString()
            };
            using var form = new ParameterForm((byte)decoder.AssignedId, title);
            activeParameterForm = form;
            try
            {
                form.ShowDialog(this);
            }
            finally
            {
                activeParameterForm = null;
            }
            // Einstellungen könnten geändert sein: Angaben in der Liste und Stromanzeige auffrischen
            RefreshDecoderInfo(decoder);
        }

        // Liest die Einstellungen eines Decoders für Liste und Hauptseite (im Hintergrund).
        // Stromdecoder: Abschaltgrenze für die Farbe der Stromanzeige.
        private void RefreshDecoderInfo(CANguruDecoder decoder)
        {
            Task.Run(() =>
            {
                decoder.UpdateFromDecoder();
                if (decoder is PowerDecoder p && p.MaxCurrent > 0)
                    PowerCtrl.Threshold_mA = p.MaxCurrent;
            });
        }

        // Kontextmenü der Decoderliste: Decoder aus der Liste der Bridge entfernen
        // (die Bridge vergisst die feste ID; meldet er sich wieder, erhält er eine neue)
        private async void DecoderEntfernen_Click(object? sender, EventArgs e)
        {
            if (listBoxDecoder.SelectedItem is not CANguruDecoder decoder)
                return;
            byte id = (byte)decoder.AssignedId;
            if (MessageBox.Show(this, $"Decoder {id} ({decoder.Type}) aus der Liste der Bridge entfernen?",
                    "Decoder entfernen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_DECODER,
                (byte)ProtocolConstants.CG2_DECODER_REMOVE, (byte)ProtocolConstants.CG2_ID_BRIDGE, new[] { id });
            if (r == null || r.IsError)
            {
                MessageBox.Show(this, "Die Bridge hat den Decoder nicht entfernt: " +
                    (r == null ? "keine Antwort." : BridgeLink.ErrorText(r.ErrorCode)),
                    "Decoder entfernen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            decoders.Remove(id);
            RefreshDecoderListBox();
            Log($"Decoder {id} entfernt.");
        }

        // Decoder-ListBox neu aufbauen (Header + Decoder)
        private void RefreshDecoderListBox()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(RefreshDecoderListBox));
                return;
            }

            listBoxDecoder.Items.Clear();
            listBoxDecoder.Items.Add("HEADER");
            foreach (var decoder in decoders.All())
                listBoxDecoder.Items.Add(decoder);
        }

        // OwnerDraw für Decoder-ListBox
        private void ListBoxDecoder_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
                return;

            var lb = (ListBox)sender;
            var item = lb.Items[e.Index];

            e.DrawBackground();

            // Header-Zeile
            if (item is string s && s == "HEADER")
            {
                using var bg = new SolidBrush(Color.LightGray);
                e.Graphics.FillRectangle(bg, e.Bounds);

                string header = "Name                      MAC                  UID";

                using var textBrush = new SolidBrush(Color.Black);
                e.Graphics.DrawString(header, lb.Font, textBrush, e.Bounds.Left + 2, e.Bounds.Top + 1);
                return;
            }


            // Normale Decoder-Zeile
            if (item is CANguruDecoder decoder)
            {
                string macStr = $"{decoder.Mac[0]:X2}:{decoder.Mac[1]:X2}:{decoder.Mac[2]:X2}:{decoder.Mac[3]:X2}:{decoder.Mac[4]:X2}:{decoder.Mac[5]:X2}";
                int hexadr = 0xd715 + decoder.AssignedId;
                string adr = $"{hexadr:X4}".PadRight(8);

                string name = decoder.Type switch
                {
                    DecoderType.Switch => "Weichendecoder",
                    DecoderType.Signal => "Signaldecoder",
                    DecoderType.Power => "Stromwaechter",
                    DecoderType.Feedback => "Gleisbesetztmelder",
                    DecoderType.LedSignal => "LED-Signaldecoder",
                    _ => "Decoder"
                };

                string line = $"{name.PadRight(25)} {macStr.PadRight(20)} {adr}";

                // 1. Hintergrundfarbe bestimmen
                Color back;
                if ((e.State & DrawItemState.Selected) == DrawItemState.Selected)
                {
                    // Benutzer hat die Zeile angeklickt → Highlight-Farbe
                    back = Color.LightBlue;   // oder SystemColors.Highlight
                }
                else
                {
                    // Normaler Zustand → DecoderStatus-Farbe
                    switch (decoder.Status)
                    {
                        case DecoderStatus.Weak:
                            back = Color.LightYellow;
                            break;
                        case DecoderStatus.Lost:
                            back = Color.LightCoral;
                            break;
                        default:
                            back = Color.White;
                            break;
                    }
                }

                using var backBrush = new SolidBrush(back);
                using var textBrush = new SolidBrush(e.ForeColor);

                // 2. Hintergrund zeichnen
                e.Graphics.FillRectangle(backBrush, e.Bounds);

                // 3. Text zeichnen
                e.Graphics.DrawString(line, lb.Font, textBrush, e.Bounds.Left + 2, e.Bounds.Top + 1);

                // 4. Fokusrechteck (optional)
                e.DrawFocusRectangle(); return;
            }

            // Fallback
            e.Graphics.DrawString(item.ToString(), lb.Font, Brushes.Black, e.Bounds.Left + 2, e.Bounds.Top + 1);
        }

        #endregion

        #region Lok-Handling (Liste, Zeichnen, Export, Manuelle Loks)

        private void UpdateNextMfxSID()
        {
            int maxSid = 0;

            foreach (var item in listBoxLoks.Items)
            {
                if (item is CANguruLok lok)
                {
                    if (!lok.IsManual) // echte mfx-Lok
                    {
                        if (lok.MfxSID > maxSid)
                            maxSid = lok.MfxSID;
                    }
                }
            }

            int nextSid = maxSid + 1;

            // UI-thread-sicher setzen
            if (UpDnnextLokAddress.InvokeRequired)
            {
                UpDnnextLokAddress.Invoke(new Action(() =>
                {
                    UpDnnextLokAddress.Value = nextSid;
                }));
            }
            else
            {
                UpDnnextLokAddress.Value = nextSid;
            }
        }

        private void RefreshLokListBox()
        {
            listBoxLoks.Items.Clear();

            listBoxLoks.Items.Add("HEADER");

            foreach (var lok in lokstorage.Loks.Values.OrderBy(l => l.Name))
                listBoxLoks.Items.Add(lok);
        }

        // OwnerDraw für Lok-ListBox
        private void listBoxLoks_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
                return;

            var lb = (ListBox)sender;
            var item = lb.Items[e.Index];

            e.DrawBackground();

            // Header-Zeile
            if (item is string s && s == "HEADER")
            {
                using var bg = new SolidBrush(Color.LightGray);
                e.Graphics.FillRectangle(bg, e.Bounds);

                string header = "Name                 Adr  Typ    UID";

                using var textBrush = new SolidBrush(Color.Black);
                e.Graphics.DrawString(header, lb.Font, textBrush, e.Bounds.Left + 2, e.Bounds.Top + 1);
                return;
            }


            // Normale Lok-Zeile
            if (item is CANguruLok lok)
            {
                string name = lok.Name.PadRight(20);
                string adr = lok.MfxSID.ToString().PadRight(4);
                string typ = (lok.IsManual ? lok.DecoderType : "mfx").PadRight(6);
                string uid = lok.UID.ToString("X8").PadRight(10);

                string line = $"{name} {adr} {typ} {uid}";

                using var textBrush = new SolidBrush(e.ForeColor);
                e.Graphics.DrawString(line, lb.Font, textBrush, e.Bounds.Left + 2, e.Bounds.Top + 1);
                return;
            }

            // Fallback
            e.Graphics.DrawString(item.ToString(), lb.Font, Brushes.Black, e.Bounds.Left + 2, e.Bounds.Top + 1);
        }

        // Lok-Liste (nur Loks, ohne Header) aktualisieren
        private void UpdateLokList()
        {
            listBoxLoks.Items.Clear();

            foreach (var lok in lokstorage.Loks.Values.OrderBy(l => l.Name))
                listBoxLoks.Items.Add(lok);
            UpdateNextMfxSID();
        }

        // Callback: Lok vollständig eingelesen
        private void OnLokCompleted(CANguruLok lok)
        {
            lokstorage.AddOrUpdate(lok);
            UpdateLokList();
        }

        // Lok löschen
        private void btnDelete_Click(object sender, EventArgs e)
        {
            if (listBoxLoks.SelectedItem is CANguruLok lok)
            {
                lokstorage.Remove(lok.UID);
                UpdateLokList();
            }
        }

        // CS2-Lokliste exportieren und zur Bridge übertragen
        private async void btnLokListe_Click(object sender, EventArgs e)
        {
            var exporter = new Cs2LokFileExporter
            {
                SessionId = CANFrames.newSubscriptionCounter,
                VersionMinor = 3
            };

            string tempFile = "lokomotive.cs2";
            exporter.Export(tempFile, lokstorage.Loks.Values);

            await exporter.UploadLokFileToBridge(ProtocolConfig.ipString, tempFile);

            Log("Lokliste exportiert und zur Bridge übertragen.");
        }

        // Manuelle Lok hinzufügen
        private void btnAddManualLok_Click(object sender, EventArgs e)
        {
            using var dlg = new ManualLokDialog();

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                lokstorage.AddOrUpdate(dlg.ResultLok);
                RefreshLokListBox();
            }
        }

        private void btnShowLokList_Click(object sender, EventArgs e)
        {
            Log("Lok-Liste anzeigen...");
            lokListForm.RefreshLokList(lokstorage.Loks.Values);
            lokListForm.Show();
            lokListForm.BringToFront();
        }

        #endregion

        #region CANguru-Steuerloop (Gleisbox-Start, PING, LoopTimer)

        // Gleisbox-Startprozedur
        private void proc_startGleisbox()
        {
            if (BridgeIPIsNotSet())
                return;

            SendCANFrame2Bridge(CANFrames.magicStart0);
            SendCANFrame2Bridge(CANFrames.magicStart1);
            SendCANFrame2Bridge(CANFrames.CAN_PING);
            // Spannung wird von WDP eingeschaltet
        }

        // PING-Timer
        private void PINGtimer_Tick(object sender, EventArgs e)
        {
            if (BridgeIPIsNotSet())
                return;

            SendCANFrame2Bridge(CANFrames.CAN_PING);
        }

        // Haupt-Loop für CANguru-Steuerlogik
        private void looptimer_Tick(object sender, EventArgs e)
        {
            if (BridgeIPIsNotSet())
                return;

            switch (canguruStatus)
            {
                case enum_canguruStatus.idle:
                    // nichts zu tun
                    break;

                case enum_canguruStatus.startGleisbox:
                    proc_startGleisbox();
                    canguruStatus = enum_canguruStatus.wait4LokListe;
                    break;

                case enum_canguruStatus.wait4LokListe:
                    // hier könnte später  andere Logik folgen
                    canguruStatus = enum_canguruStatus.systemIsRunning;

                    break;
                case enum_canguruStatus.systemIsRunning:
                    looptimer.Stop();
                    break;
            }
        }

        #endregion

        #region UI-Eventhandler (Buttons, UpDowns)

        // Verbinden mit Bridge
        private void btnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                btnConnect.Enabled = false;
                btnConnect.Text = "Verbinde ..."; // "Verbunden", sobald die Bridge antwortet (HandleBridgeFound)

                session.Start();

                Log(string.Empty);
                Log("Verbinde mit CANguru-Bridge ...");
            }
            catch (Exception ex)
            {
                Log("Fehler: " + ex.Message);
                btnConnect.Text = "Verbinden";
                btnConnect.Enabled = true;
            }
        }

        private void close_GUI()
        {
            try
            {
                guiClosing = true;
                session.Stop();
                Log(".");
                Log("Programm wird beendet...");
                SendCANFrame2Bridge(CANFrames.SysSTOPP);

                lblBRIDGEStatus.Text = "Offline";
                lblBRIDGEStatus.ForeColor = Color.Red;

                // 2 Sekunden warten
                System.Threading.Thread.Sleep(2000);

                Properties.Settings.Default.newSubscriptionCounter = (byte)UpDnnewSubscriptionCounter.Value;
                Properties.Settings.Default.nextLokAddress = (byte)UpDnnextLokAddress.Value;
                Properties.Settings.Default.Save();
                // Sitzungsende: Bridge hält die Gleisbox an, setzt die Decoder zurück und startet neu
                BridgeLink.Instance.Send((byte)ProtocolConstants.CG2_F_SESSION, (byte)ProtocolConstants.CG2_SESSION_END,
                                         (byte)ProtocolConstants.CG2_ID_BRIDGE, null);
            }
            catch
            {
                // Ignorieren, beim Beenden nicht kritisch
            }

            Application.Exit();
        }

        // Programm beenden
        private void btnExit_Click(object sender, EventArgs e)
        {
            close_GUI();
        }

        // UpDown: neuer Subscription-Counter
        private void UpDnnewSubscriptionCounter_ValueChanged(object sender, EventArgs e)
        {
            CANFrames.newSubscriptionCounter = (byte)UpDnnewSubscriptionCounter.Value;
        }

        // UpDown: nächste Lokadresse
        private void UpDnnextLokAddress_ValueChanged(object sender, EventArgs e)
        {
            CANFrames.nextLokAddress = (byte)UpDnnextLokAddress.Value;
        }
        private void ApplyNextMfxSIDToUI()
        {
            if (lokstorage == null)
                return;

            int next = lokstorage.NextMfxSID;

            // UI-thread-sicher
            if (UpDnnextLokAddress.InvokeRequired)
            {
                UpDnnextLokAddress.Invoke(new Action(() =>
                {
                    UpDnnextLokAddress.Value = next;
                }));
            }
            else
            {
                UpDnnextLokAddress.Value = next;
            }
        }

        // CANguru-Loks anstoßen (MFX-Scan)
        private void btnCANguruLoks_Click_1(object sender, EventArgs e)
        {
            listBoxMFX.Clear();
            STOPP_Timer.Enabled = true;
            mfxParser.handleCANguruLoks(CANFrames.newSubscriptionCounter);
        }

        // STOPP-Timer: System GO nach kurzer Zeit
        private void STOPP_Timer_Tick(object sender, EventArgs e)
        {
            SendCANFrame2Bridge(CANFrames.SysGO);
            STOPP_Timer.Enabled = false;
        }

        #endregion

        #region Parser-Callbacks und Logging

        // Parser: nächste Lok-ID
        private void OnincnextLocid(byte val)
        {
            CANFrames.nextLokAddress++;
            UpDnnextLokAddress.Value = CANFrames.nextLokAddress;
        }

        // Parser: bisherige Adresse einer bereits bekannten mfx-Lok (null = neue Lok)
        private byte? FindKnownMfxSid(uint uid)
        {
            if (lokstorage != null &&
                lokstorage.Loks.TryGetValue(uid, out var lok) &&
                !lok.IsManual && lok.MfxSID != 0)
                return lok.MfxSID;
            return null;
        }

        // Parser: Log-Meldung
        public void OnParserLog(string msg)
        {
            Log(msg);
        }

        // Parser: CAN-Frame soll gesendet werden
        private void OnSendFrameRequested(byte[] data)
        {
            if (BridgeIPIsNotSet())
                return;

            SendCANFrame2Bridge(data);
        }

        // Text ins Log-Fenster schreiben
        public void Log(string text)
        {
            Invoke(new Action(() =>
            {
                tbMessages.AppendText(text + "\r\n");
            }));
        }

        #endregion

        #region Utility-Methoden (Thread-Sicherheit, Senden)

        // Thread-sicherer UI-Aufruf
        private void UiInvoke(Action action)
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }

        #endregion
        private void BtnStopGo_StopClicked(object sender, EventArgs e)
        {
            Log("Stopp gedrückt");
            SendCANFrame2Bridge(CANFrames.SysSTOPP);
            //    btnStopGo.PerformClick();
        }

        private void BtnStopGo_GoClicked(object sender, EventArgs e)
        {
            Log("GO gedrückt");
            SendCANFrame2Bridge(CANFrames.SysGO);
            //   btnStopGo.PerformClick();
        }

        private async void showDecoderList_Click(object sender, EventArgs e)
        {
            Log("Decoder-Liste anzeigen...");

            // 1. Live-Werte holen
            foreach (var dec in decoders.All())
            {
                // 2. Decoder Zeit geben, sich zu initialisieren
                await Task.Delay(150);
                dec.UpdateFromDecoder();
            }

            // 3. ListView neu aufbauen
            decoderListForm.LoadDecoderList();

            // 4. Anzeigen
            decoderListForm.Show();
        }
    }
}
