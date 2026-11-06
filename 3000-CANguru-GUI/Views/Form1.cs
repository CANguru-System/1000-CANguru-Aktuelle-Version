using CANguru;
using CANguru.Communication;
using CANguru.Controllers;
using CANguru.Controls;
using CANguru.GUI.Forms;
using CANguru.GUI.Panels;
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
using static CANguru.Controllers.DecoderStorage;
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
        private DecoderStorage decoderStorage;
        private DecoderList decoderController;
        private SwitchDecoderSettingsForm activeSwitchForm;
        private SignalDecoderSettingsForm activeSignalForm;
        private FeedbackDecoderSettingsForm activeFeedbackForm;
        private volatile ParameterForm? activeParameterForm; // offenes allgemeines Parameter-Fenster
        private DecoderListForm decoderListForm;
        private LokListForm lokListForm;

        // UDP
        private UdpClient udpReceiver;

        // Firmware-Upload
        private CANguru.OTA.OtaUploader otaUploader;
        private CancellationTokenSource _uploadCts;
        private byte lastPercent = 0;
        private int lastOtaDecoder = -1; // Decoder, dessen OTA-Fortschritt zuletzt kam
        private int lastOtaPercent = -1; // zuletzt gemeldeter Fortschritt dieses Decoders

        // lokomotive.cs2 -Upload
        private Cs2FileUploader uploader;

        // Bridge suchen (Protokoll Version 2: SESSION DISCOVER)
        private readonly System.Windows.Forms.Timer discoverTimer = new System.Windows.Forms.Timer();
        private bool bridgeFound = false;

        // Alive-Überwachung
        private DecoderStatus currentDecoderStatus = DecoderStatus.Lost;
        private bool guiClosing = false;
        private bool isFirmwareUploadActive = false;
        private AliveManager aliveManager = new AliveManager();

        private int lastPower = -1;
        private int lastSwitch = -1;
        private int lastSignal = -1;
        private int lastFeedback = -1;

        // CAN-Queues
        private readonly ConcurrentQueue<(uint seq, byte[] data)> incomingFrames = new();

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

            // Position irgendwo sinnvoll platzieren:
            dualBar.Width = 100;
            dualBar.Height = 105;
            dualBar.Left = 500;   // z.B. an Stelle der alten Bars
            dualBar.Top = 450;

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
            decoderController = new DecoderList();
            lokListForm = new LokListForm();
            pictureBox1.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap();
            GuiLogger.MessageLogged += OnGuiLog;
            // AliveManager initialisieren
            aliveManager = new AliveManager();

            // Hier kommt die Registrierung hin:
            aliveManager.AliveSummaryReceived += (power, sw, signal, feedback) =>
            {
                lastPower = UpdateAliveField(cntPower, lastPower, power);
                lastSwitch = UpdateAliveField(cntSwitch, lastSwitch, sw);
                lastSignal = UpdateAliveField(cntSignal, lastSignal, signal);
                lastFeedback = UpdateAliveField(cntFeedback, lastFeedback, feedback);
            };
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
            decoderStorage = new DecoderStorage();

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
            decoderListForm = new DecoderListForm(decoderStorage);
            // OTA-Firmaware
            otaUploader = new CANguru.OTA.OtaUploader(
                log: text => Log(text),
                setPercent: percent => UiInvoke(() =>
                {
                    dualBar.CurrentValue = percent;
                    dualBar.CurrentText = $"{percent}%";
                    dualBar.Invalidate();
                }),
                blockSize: ProtocolConstants.CG2_OTA_BLOCK_SIZE
            );

            // Bridge suchen: DISCOVER per Broadcast, bis sie antwortet
            discoverTimer.Interval = 1000;
            discoverTimer.Tick += (s, e) => SendDiscover();
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

        // UDP-Start: GUI-Port öffnen, Receiver starten
        private void StartUdp()
        {
            // GUI hört auf GUI_PORT
            udpReceiver = new UdpClient(ProtocolConstants.GUI_PORT);
            udpReceiver.BeginReceive(OnUdpData, null);

            // Bridge suchen; wiederholt jede Sekunde, bis sie antwortet
            bridgeFound = false;
            SendDiscover();
            UiInvoke(() => discoverTimer.Start());
        }

        // SESSION DISCOVER per Broadcast [protoMinor]
        private void SendDiscover()
        {
            if (bridgeFound)
            {
                discoverTimer.Stop();
                return;
            }
            BridgeLink.Instance.Broadcast((byte)ProtocolConstants.CG2_F_SESSION,
                                          (byte)ProtocolConstants.CG2_SESSION_DISCOVER,
                                          new byte[] { (byte)ProtocolConstants.CG2_PROTO_MINOR });
        }

        // Zum Senden an die Bridge
        public void SendToBridge(byte[] frame)
        {
            UdpClientSingleton.Instance.Send(frame);
        }

        private void OnUdpData(IAsyncResult ar)
        {
            if (guiClosing)
                return;

            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            byte[] data;

            try
            {
                data = udpReceiver.EndReceive(ar, ref remote);
            }
            catch (ObjectDisposedException)
            {
                // Socket wurde geschlossen → normal beim Beenden
                return;
            }
            catch (SocketException)
            {
                // UDP wurde abgebrochen → normal beim Beenden
                return;
            }

            // Wenn GUI gerade geschlossen wird → keine Verarbeitung mehr
            if (guiClosing)
                return;

            ProcessIncomingPacket(data, remote);

            try
            {
                udpReceiver.BeginReceive(OnUdpData, null);
            }
            catch
            {
                // Beim Schließen kann auch BeginReceive fehlschlagen → ignorieren
            }
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

        // Antwort auf DISCOVER: [fwMajor][fwMinor][protoMinor][gleisbox 0/1]
        // Die Bridge meldet sich auch, wenn noch kein Decoder läuft.
        private async void HandleBridgeFound(IPAddress bridgeIp, byte[] payload)
        {
            if (bridgeFound)
                return;
            bridgeFound = true;
            UiInvoke(() => discoverTimer.Stop());

            var r = new Cg2Reader(payload);
            byte fwMajor = r.U8(), fwMinor = r.U8(), protoMinor = r.U8(), gleisbox = r.U8();

            currentDecoderStatus = DecoderStatus.Healthy;
            Log($"Bridge gefunden: {bridgeIp}, Firmware {fwMajor}.{fwMinor}, Protokoll 2.{protoMinor}" +
                (gleisbox == 1 ? ", Gleisbox erkannt" : ""));

            // Ab jetzt direkt an die Bridge senden statt per Broadcast
            ProtocolConfig.BridgeIP = bridgeIp;
            ProtocolConfig.ipString = bridgeIp.ToString();
            UdpClientSingleton.Instance.SetBridge(bridgeIp);
            UiInvoke(() => txtBridgeIP.Text = ProtocolConfig.ipString);
            UiInvoke(() => btnStopGo.Enabled = true);
            UiInvoke(() => btnConnect.Text = "Verbunden");

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

        // Nachrichten im Protokoll Version 2 (Sitzung, CAN, OTA)
        private void ProcessCg2Packet(byte[] data, IPEndPoint remote)
        {
            if (Cg2.Decode(data, out var h, out var payload) != Cg2DecodeResult.Ok)
            {
                Log($"Ungültige Nachricht von der Bridge ignoriert: {BitConverter.ToString(data)}");
                return;
            }

            // Antworten auf eigene Anfragen gehen an den Wartenden in BridgeLink
            if (BridgeLink.Instance.HandleResponse(h, payload))
                return;

            switch (h.Feature)
            {
                case ProtocolConstants.CG2_F_SESSION:
                    if (h.Command == ProtocolConstants.CG2_SESSION_DISCOVER_REPLY)
                        HandleBridgeFound(remote.Address, payload);
                    return;

                case ProtocolConstants.CG2_F_CAN:
                    // [folge u32][frame 13][quelle] -> Frame mit Quelle (14 Byte) zur Anzeige
                    if (h.Command == ProtocolConstants.CG2_CAN_TO_GUI &&
                        payload.Length >= 4 + ProtocolConstants.CAN_FRAME_SIZE + 1)
                    {
                        uint seq = BitConverter.ToUInt32(payload, 0);
                        incomingFrames.Enqueue((seq, payload[4..]));
                    }
                    return;

                case ProtocolConstants.CG2_F_OTA:
                    if (h.Command == ProtocolConstants.CG2_OTA_PROGRESS)
                        HandleOtaProgress(payload);
                    else if (h.Command == ProtocolConstants.CG2_OTA_RESULT)
                        HandleOtaResultFromBridge(payload);
                    return;

                case ProtocolConstants.CG2_F_DECODER:
                    // ADDED: [id][typ][mac 6][fwMajor][fwMinor][protoMinor][reserviert]
                    if (h.Command == ProtocolConstants.CG2_DECODER_ADDED && payload.Length >= 10)
                        HandleDecoderAdded(payload[0], (DecoderType)payload[1], payload[2..8],
                                           payload[8], payload[9]);
                    return;

                case ProtocolConstants.CG2_F_ALIVE:
                    if (h.Command == ProtocolConstants.CG2_ALIVE_ALIVE)
                        HandleDecoderAlive(h.Decoder, payload);
                    else if (h.Command == ProtocolConstants.CG2_ALIVE_SUMMARY)
                        HandleAliveSummary(payload);
                    return;

                case ProtocolConstants.CG2_F_POWER:
                    HandlePowerMessage(h, payload);
                    return;

                case ProtocolConstants.CG2_F_EVENT:
                    // Ereignis eines Decoders (Stellung, Kalibrierung ...): [ereignis][daten]
                    if (h.Command == ProtocolConstants.CG2_EVENT_EVENT && payload.Length >= 1)
                        activeParameterForm?.OnEvent(h.Decoder, payload[0], payload[1..]);
                    return;

                case ProtocolConstants.CG2_F_STEPPER:
                    // Meldungen von Weiche/Formsignal an das gerade offene Einstellungsfenster
                    activeSwitchForm?.HandleSetEndAck(h.Decoder, h.Command, payload);
                    activeSwitchForm?.HandleSetCurrPos(h.Decoder, h.Command, payload);
                    activeSignalForm?.HandleSetEndAck(h.Decoder, h.Command, payload);
                    activeSignalForm?.HandleSetCurrPos(h.Decoder, h.Command, payload);
                    return;
            }
        }

        // ALIVE eines Decoders: [status][anzahl][wert u16] x anzahl
        private void HandleDecoderAlive(byte decoderId, byte[] payload)
        {
            var r = new Cg2Reader(payload);
            byte status = r.U8();
            int count = r.U8();
            var values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = r.U16();
            if (!r.Ok)
                return;

            if (!decoderStorage.Decoders.TryGetValue(decoderId, out var dec))
                return;
            dec.LastSeen = DateTime.Now; // den Status berechnet aliveTimer aus LastSeen

            // offenes Parameter-Fenster zeigt die Messwerte laut Beschreibung an
            activeParameterForm?.UpdateValues(decoderId, values);

            // Stromdecoder: Messwerte = Strom je Kanal in mA, 0xFFFF = nicht angeschlossen
            if (dec.Type == DecoderType.Power && count >= 2)
            {
                PowerCtrl.UpdateCurrent(0, values[0] == 0xFFFF ? -1 : values[0]);
                PowerCtrl.UpdateCurrent(1, values[1] == 0xFFFF ? -1 : values[1]);
            }
        }

        // SUMMARY: [anzahl][typ][zahl] x anzahl - bereite Decoder je Typ
        private void HandleAliveSummary(byte[] payload)
        {
            if (payload.Length < 1)
                return;
            int power = 0, sw = 0, signal = 0, feedback = 0;
            int n = payload[0];
            for (int i = 0; i < n && 2 + 2 * i < payload.Length; i++)
            {
                byte typ = payload[1 + 2 * i];
                int zahl = payload[2 + 2 * i];
                if (typ == ProtocolConstants.DEVTYPE_POWER) power = zahl;
                else if (typ == ProtocolConstants.DEVTYPE_SWITCH) sw = zahl;
                else if (typ == ProtocolConstants.DEVTYPE_SIGNAL) signal = zahl;
                else if (typ == ProtocolConstants.DEVTYPE_RM) feedback = zahl;
            }
            aliveManager.ProcessAliveSummary(power, sw, signal, feedback);
        }

        // Feature Strom: Gleisspannung (TRACK [an]) und Überstrom (SHUTDOWN [kanal][mA u16])
        private void HandlePowerMessage(Cg2Header h, byte[] payload)
        {
            var r = new Cg2Reader(payload);
            if (h.Command == ProtocolConstants.CG2_POWER_TRACK)
            {
                byte on = r.U8();
                if (!r.Ok)
                    return;
                if (on == 0)
                {
                    Log("STOPP – Gleisspannung ausgeschaltet.");
                    UiInvoke(() => btnStopGo.SetMode(false));
                }
                else
                {
                    Log("GO – Gleisspannung liegt an.");
                    UiInvoke(() => btnStopGo.SetMode(true));
                }
            }
            else if (h.Command == ProtocolConstants.CG2_POWER_SHUTDOWN)
            {
                int channel = r.U8();
                ushort mA = r.U16();
                if (!r.Ok)
                    return;
                PowerCtrl.UpdateCurrent(channel, mA);
                PowerCtrl.TriggerShutdown(channel);
                Log($"ÜBERSTROM auf Kanal {channel}: {mA} mA – Gleisspannung abgeschaltet.");
                // Knopf auf GO stellen, damit ein Klick wieder einschaltet
                UiInvoke(() => btnStopGo.SetMode(false));
            }
        }

        // OTA-Fortschritt: [decoderId][prozent][gesamtProzent]
        private void HandleOtaProgress(byte[] payload)
        {
            if (payload.Length < 3)
                return;
            int id = payload[0], percent = payload[1], overall = Math.Min((int)payload[2], 100);

            // 100 % gesamt = alle Decoder bearbeitet (auch nach einem Fehlschlag)
            if (overall >= 100 && percent < 100)
            {
                UiInvoke(() => dualBar.Visible = false);
                return;
            }

            if (id != lastOtaDecoder)
            {
                lastOtaDecoder = id;
                lastOtaPercent = -1;
                Log($"Decoder {id} lädt Firmware ...");
            }
            else if (percent == 0 && lastOtaPercent > 0)
                Log($"Decoder {id}: neuer Versuch ...");
            bool fertigUebertragen = percent == 100 && lastOtaPercent != 100;
            lastOtaPercent = percent;
            UiInvoke(() =>
            {
                dualBar.Visible = true;
                dualBar.CurrentValue = percent;
                dualBar.CurrentText = $"{percent}%";
                dualBar.TotalValue = overall;
                dualBar.TotalText = $"{overall}%";
                dualBar.Invalidate();
            });
            if (fertigUebertragen)
                // Ob das Update gelungen ist, steht erst nach Prüfung und Neustart fest (OTA_RESULT)
                Log("Firmware an den Decoder übertragen – Decoder prüft und startet neu ...");
            if (overall >= 100)
                UiInvoke(() => dualBar.Visible = false);
        }

        // Ergebnis des OTA-Updates eines Decoders
        // payload: [decoderId, result, grund]  result: 0 = ok, 1 = fehlgeschlagen, 2 = keine Rückmeldung
        // grund: CG2_OTA_FAIL_* (warum der Decoder das Update nicht übernommen hat)
        void HandleOtaResultFromBridge(byte[] payload)
        {
            if (payload.Length < 2) return;

            byte decoderId = payload[0];
            byte result = payload[1];
            int reason = payload.Length >= 3 ? payload[2] : ProtocolConstants.CG2_OTA_FAIL_NONE;

            if (result == 0)
            {
                Log($"OTA erfolgreich für Decoder {decoderId}.");
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
            Log($"OTA FEHLGESCHLAGEN für Decoder {decoderId}: {grund}");

            UiInvoke(() => MessageBox.Show(
                this,
                $"Das Update für Decoder {decoderId} ist fehlgeschlagen.\r\n\r\n{grund}\r\n\r\n" +
                (result == 1
                    ? "Der Decoder arbeitet mit der bisherigen Firmware weiter."
                    : "Bitte prüfen, ob der Decoder in der Liste wieder erscheint; sonst kurz stromlos machen."),
                "Firmware-Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning));
        }
        // Ein fehlerhaftes Paket von der Bridge darf die GUI nicht beenden:
        // die Auswertung läuft im UDP-Thread, eine Ausnahme dort beendet sonst den Prozess.
        private void ProcessIncomingPacket(byte[] data, IPEndPoint remote)
        {
            try
            {
                ProcessIncomingPacketCore(data, remote);
            }
            catch (Exception ex)
            {
                if (guiClosing)
                    return;
                try
                {
                    Log($"Fehlerhaftes Paket von der Bridge ignoriert ({ex.GetType().Name}): {BitConverter.ToString(data)}");
                }
                catch
                {
                    // Fenster wird gerade geschlossen
                }
            }
        }

        private void ProcessIncomingPacketCore(byte[] data, IPEndPoint remote)
        {
            if (guiClosing)
                return;

            // Protokoll Version 2 (Sitzung, CAN, Datei, OTA); erkennbar am ersten Byte 0xC2
            if (Cg2.IsV2(data))
            {
                ProcessCg2Packet(data, remote);
                return;
            }

            // Altes Format wird seit Protokoll Version 2 nicht mehr ausgewertet;
            // alte Decoder übersetzt die Bridge.
            Debug.WriteLine($"Nachricht im alten Format von {remote} ignoriert ({data.Length} Bytes).");
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
                    // Zubehör-Decoder (Phase 3): Einstellungen und Kalibrierung über das
                    // Parameter-Fenster. Das Fenster mit Weichenbild kommt in Phase 5 zurück.
                    OpenParameterForm(decoder);
                    return;
                case DecoderType.Feedback:
                    {
                        byte feedbackassignedId = (byte)decoder.AssignedId;

                        activeFeedbackForm = new FeedbackDecoderSettingsForm(feedbackassignedId);
                        activeFeedbackForm.ShowDialog(this);
                        activeFeedbackForm = null;
                        return;
                    }
                    return;
                case DecoderType.Power:
                    byte powerAssignedId = (byte)decoder.AssignedId;
                    new PowerDecoderSettingsForm(powerAssignedId).ShowDialog(this);
                    return; // weiter zu Power-Einstellungen
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

        private void UpdateProgressSafe(int current, int total)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateProgressSafe(current, total)));
                return;
            }

            int percent = (int)(current * 100.0 / total);
            Log($"Progress: {percent}% ({current}/{total})");
            if (percent > lastPercent)
                lastPercent++;

            this.BeginInvoke(new Action(() =>
            {
                dualBar.CurrentValue = lastPercent;
                dualBar.CurrentText = $"{lastPercent}%";
                dualBar.Invalidate();
            }));
            //    dualBar.CurrentValue = percent;
        }

        private async void btnOtaUpdate_Click(object sender, EventArgs e)
        {
            byte id = 0;

            // Einzeldecoder auswählen
            if (radioSingle.Checked)
            {
                if (listBoxDecoder.SelectedItem is not CANguruDecoder decoder)
                {
                    MessageBox.Show("Bitte einen Decoder auswählen.");
                    return;
                }
                id = (byte)decoder.AssignedId;
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
            lastPercent = 0;
            isFirmwareUploadActive = true;
            StopAllGuiTimers();

            _uploadCts = new CancellationTokenSource();
            bool ok = false;

            try
            {
                // ⭐ NEU: OTA-Uploader benutzen
                ok = await otaUploader.UploadFirmwareAsync(firmware, _uploadCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("Firmware-Upload abgebrochen.");
            }
            catch (Exception ex)
            {
                Log($"Firmware-Upload Fehler: {ex.Message}");
            }

            isFirmwareUploadActive = false;
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

            // OTA-Start an die Bridge: [modus][id oder typ], modus 0 = ein Decoder, 1 = alle eines Typs
            byte mode, selector;
            if (radioSingle.Checked)
            {
                dualBar.SingleBarMode = true;
                mode = 0;
                selector = id;
            }
            else
            {
                byte? type = null;
                if (radioType1.Checked) type = ProtocolConstants.DEVTYPE_SWITCH;
                else if (radioType2.Checked) type = ProtocolConstants.DEVTYPE_SIGNAL;
                else if (radioType3.Checked) type = ProtocolConstants.DEVTYPE_POWER;
                else if (radioType4.Checked) type = ProtocolConstants.DEVTYPE_RM;

                if (type == null)
                {
                    MessageBox.Show("Bitte einen OTA-Modus auswählen.");
                    return;
                }
                dualBar.SingleBarMode = false;
                mode = 1;
                selector = type.Value;
            }

            lastOtaDecoder = -1;
            var start = await BridgeLink.Instance.RequestAsync(
                (byte)ProtocolConstants.CG2_F_OTA, (byte)ProtocolConstants.CG2_OTA_START,
                (byte)ProtocolConstants.CG2_ID_BRIDGE, new byte[] { mode, selector });

            if (start == null || start.IsError)
            {
                dualBar.Visible = false;
                string grund = start == null ? "Die Bridge antwortet nicht."
                    : start.ErrorCode == ProtocolConstants.CG2_ERR_UNKNOWN_DECODER
                        ? (mode == 0 ? "Der Decoder ist der Bridge nicht bekannt." : "Kein Decoder dieses Typs angemeldet.")
                        : BridgeLink.ErrorText(start.ErrorCode);
                Log("OTA-Start nicht möglich: " + grund);
                MessageBox.Show(this, grund, "Firmware-Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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

        public void OnDecoderAlive(int decoderId)
        {
            if (decoderStorage.Decoders.TryGetValue((uint)decoderId, out var decoder))
            {
                decoder.LastSeen = DateTime.Now;
            }
        }

        // Alive-Statusanzeige
        private void UpdateUI()
        {
            var decoderSnapshot = decoderStorage.Decoders.Values.ToList();

            foreach (var decoder in decoderSnapshot)
            {
                switch (decoder.IsReady)
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
            foreach (var decoder in decoderStorage.Decoders.Values)
            {
                var delta = DateTime.Now - decoder.LastSeen;

                if (delta.TotalSeconds < 2)
                {
                    decoder.IsReady = DecoderStatus.Healthy;
                }
                else if (delta.TotalSeconds < 5)
                {
                    decoder.IsReady = DecoderStatus.Weak;
                }
                else
                {
                    decoder.IsReady = DecoderStatus.Lost;
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

        // Eingehende CAN-Frames aus Queue holen, sortieren und verarbeiten
        private void ProcessPendingCanFrames()
        {
            if (incomingFrames.IsEmpty)
                return;

            List<(uint seq, byte[] data)> list = new();
            while (incomingFrames.TryDequeue(out var item))
                list.Add(item);

            list.Sort((a, b) => a.seq.CompareTo(b.seq));

            foreach (var frame in list)
            {
                DisplayCanFrame(frame.data);
                mfxParser.ProcessCanFrame(frame.data);
            }
        }

        // CAN-Frame als Text darstellen
        private void DisplayCanFrame(byte[] data)
        {
            string strFrame;
            byte currCMD = data[0x01];
            byte subCmd = data[0x09];
            byte dlc = data[0x04];
            char source = (char)data[0x0D];

            strFrame = System.String.Format("{0}-{1:X2}({2:X2}){3:X2}{4:X2}", source, data[0], currCMD, data[2], data[3]);

            if ((currCMD & 0x01) == 0x01)
                strFrame += " R ";
            else
                strFrame += "   ";

            strFrame += System.String.Format("[{0}]", dlc);

            for (byte i = 5; i < 5 + dlc; i++)
                strFrame += System.String.Format(" {0:X2}", data[i]);

            if (dlc < 8)
            {
                strFrame += System.String.Format("({0:X2}", data[(byte)(5 + dlc)]);
                for (byte i = (byte)(6 + dlc); i < 13; i++)
                    strFrame += System.String.Format(" {0:X2}", data[i]);
                strFrame += ") ";
            }
            else
            {
                strFrame += "  ";
            }

            strFrame += "  ";

            byte[] bytes = new byte[8];
            for (int i = 5; i < 13; i++)
                bytes[i - 5] = data[i];

            char[] chars = Encoding.UTF8.GetChars(bytes);
            for (int i = 0; i < 8; i++)
            {
                if (chars[i] < ' ' || chars[i] > 'z')
                    strFrame += ".";
                else
                    strFrame += chars[i];
            }

            switch (currCMD)
            {
                case 0x00:
                    switch (subCmd)
                    {
                        case 0x00: strFrame += "Sys STOPP"; break;
                        case 0x01: strFrame += "Sys GO"; break;
                        case 0x02: strFrame += "Sys Halt"; break;
                        case 0x03: strFrame += "Lok Halt"; break;
                        case 0x04: strFrame += "Lok Stop"; break;
                        case 0x05: strFrame += "Lok Daten"; break;
                        case 0x06: strFrame += "Schaltzeit"; break;
                        case 0x07: strFrame += "Fast Read"; break;
                        case 0x08: strFrame += "Glsproto"; break;
                        case 0x09: strFrame += "Anm.zähler"; break;
                        case 0x0A: strFrame += "Überlast"; break;
                        case 0x0B: strFrame += "Status"; break;
                        case 0x0C: strFrame += "Kennung"; break;
                        case 0x30: strFrame += "Mfx Seek"; break;
                        case 0x80: strFrame += "Reset"; break;
                    }
                    break;
                case 0x02: strFrame += "Discovery"; break;
                case 0x04: strFrame += "MFX Bind"; break;
                case 0x06: strFrame += "MFX Verify"; break;
                case 0x08: strFrame += "Lok Speed"; break;
                case 0x0A: strFrame += "Richtung"; break;
                case 0x0C: strFrame += "Funktion"; break;
                case 0x0E: strFrame += "Read Config"; break;
                case 0x10: strFrame += "Write Config"; break;
                case 0x16: strFrame += "Zub Schalten"; break;
                case 0x18: strFrame += "Zub Konfig"; break;
                case 0x20: strFrame += "S88 Feedback"; break;
                case 0x22: strFrame += "S88 Event"; break;
                case 0x24: strFrame += "SX1 Event"; break;
                case 0x30: strFrame += "Ping"; break;
                case 0x32: strFrame += "Update"; break;
                case 0x34: strFrame += "Config Data"; break;
                case 0x36: strFrame += "Magic"; break;
                case 0x38: strFrame += "Bootloader"; break;
                case 0x3A: strFrame += "Send Konfi"; break;
                case 0x40: strFrame += "Data Query"; break;
                case 0x42: strFrame += "Data Stream"; break;
                case 0x44: strFrame += "6021 adapter"; break;
                default:
                    if (currCMD % 2 == 0)
                        strFrame += "unbekannt";
                    else
                        strFrame += System.String.Format("RE: {0:X2}", currCMD);

                    const int maxString = 60;
                    if (strFrame.Length > maxString)
                        strFrame = strFrame.Substring(0, maxString);
                    break;
            }

            UiInvoke(() =>
                {
                    listBoxCAN.AppendText(strFrame);
                    listBoxCAN.AppendText(Environment.NewLine);
                    if (currCMD < 0x08 && currCMD > 0x00)
                    {
                        listBoxMFX.AppendText(strFrame);
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
                DecoderType.Switch => new SwitchDecoder(),
                DecoderType.Signal => new SignalDecoder(),
                DecoderType.Feedback => new FeedbackDecoder(),
                _ => new CANguruDecoder()
            };

            decoder.AssignedId = id;
            decoder.Type = type;
            decoder.Mac = mac;
            decoder.Firmware = $"{fwMajor}.{fwMinor}";
            decoder.LastSeen = DateTime.Now;

            Log($"Decoder angemeldet: ID={id}, Typ={type}, Firmware {decoder.Firmware}");

            decoderStorage.AddOrUpdate(decoder);
            RefreshDecoderListBox();

            // Einstellungen holen; der Stromdecoder liefert seine Werte per ALIVE
            if (type != DecoderType.Power)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(300); // Decoder Zeit geben
                    switch (decoder)
                    {
                        case SwitchDecoder s: s.UpdateFromDecoder(); break;
                        case SignalDecoder s: s.UpdateFromDecoder(); break;
                        case FeedbackDecoder f: f.UpdateFromDecoder(); break;
                    }
                });
            }
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
            decoderStorage.Remove(id);
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
            listBoxDecoder.Items.Add("SEPARATOR");
            foreach (var decoder in decoderStorage.Decoders.Values.OrderBy(l => l.Name))
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

            // Separator-Zeile
            if (item is string sep && sep == "SEPARATOR")
            {
                using var bg = new SolidBrush(Color.Gray);
                e.Graphics.FillRectangle(bg, e.Bounds);

                string line = "---------------------------------------------------";

                using var textBrush = new SolidBrush(Color.White);
                e.Graphics.DrawString(line, lb.Font, textBrush, e.Bounds.Left + 2, e.Bounds.Top + 1);
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
                    switch (decoder.IsReady)
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
            listBoxLoks.Items.Add("SEPARATOR");

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

            // Separator-Zeile
            if (item is string sep && sep == "SEPARATOR")
            {
                using var bg = new SolidBrush(Color.Gray);
                e.Graphics.FillRectangle(bg, e.Bounds);

                string line = "-------------------------------------------";

                using var textBrush = new SolidBrush(Color.White);
                e.Graphics.DrawString(line, lb.Font, textBrush, e.Bounds.Left + 2, e.Bounds.Top + 1);
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

                StartUdp();

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

        // CAN-Log in Zwischenablage kopieren
        private void btnCopy_Click(object sender, EventArgs e)
        {
            Clipboard.SetText(listBoxCAN.Text);
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
            foreach (var dec in decoderStorage.Decoders.Values)
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
