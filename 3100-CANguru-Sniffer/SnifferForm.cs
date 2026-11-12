using CANguru.Protocol;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CANguru.Sniffer
{
    // Listens to the CAN bus through the bridge.
    //
    // The sniffer registers with the bridge exactly like the GUI does (SESSION DISCOVER),
    // so the bridge forwards every frame it receives from the CAN bus (CG2_F_CAN /
    // CG2_CAN_TO_GUI). It never sends anything else: no frame onto the bus, no SESSION
    // START (decoders stay quiet) and no SESSION END (the bridge does not restart when
    // the sniffer is closed). It is a pure listener.
    //
    // Note: the bridge does not receive the frames it puts on the bus itself, so commands
    // from Win-Digipet or the GUI do not appear here - only what other devices say
    // (track box, Mobile Station ...).
    public sealed class SnifferForm : Form
    {
        private sealed class Entry
        {
            public int Nr;
            public DateTime Time;
            public uint Seq;
            public MaerklinFrame? Frame;   // null: marker line
            public string Text = "";       // marker text or note
            public string Meaning = "";
        }

        // ---- UI ----
        private readonly Button btnConnect = new() { Text = "Verbinden", AutoSize = true };
        private readonly Button btnPause = new() { Text = "Pause", AutoSize = true, Enabled = false };
        private readonly Button btnMarker = new() { Text = "Markierung …", AutoSize = true };
        private readonly Button btnClear = new() { Text = "Leeren", AutoSize = true };
        private readonly Button btnSave = new() { Text = "Speichern …", AutoSize = true };
        private readonly CheckBox chkHidePing = new() { Text = "Ping ausblenden", AutoSize = true, Checked = true };
        private readonly CheckBox chkAutoScroll = new() { Text = "mitlaufen", AutoSize = true, Checked = true };
        private readonly Label lblBridge = new() { Text = "Bridge: nicht verbunden", AutoSize = true, Padding = new Padding(12, 6, 0, 0) };
        private readonly ListView list = new()
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            VirtualMode = true,
            Font = new Font("Consolas", 9f)
        };
        private readonly StatusStrip statusStrip = new();
        private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

        // ---- data ----
        private readonly List<Entry> entries = new();
        private readonly List<int> visible = new();      // indices into entries
        private readonly ConcurrentQueue<Entry> incoming = new();
        private readonly System.Windows.Forms.Timer drainTimer = new() { Interval = 100 };
        private System.Threading.Timer? discoverTimer;
        private UdpClient? receiver;
        private UdpClient? sender;
        private IPAddress? bridgeIp;
        private volatile bool paused;
        private volatile bool closing;
        private uint? lastSeq;
        private int nextNr = 1;
        private int lostFrames;
        private bool unsaved;
        private DateTime startTime;

        public SnifferForm()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Text = $"CANguru-Sniffer {v?.Major}.{v?.Minor} – CAN-Bus mitlauschen";
            Size = new Size(1150, 700);
            MinimumSize = new Size(700, 300);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(6),
                WrapContents = false
            };
            bar.Controls.AddRange(new Control[] { btnConnect, btnPause, btnMarker, btnClear, btnSave, chkHidePing, chkAutoScroll, lblBridge });
            foreach (var c in new Control[] { chkHidePing, chkAutoScroll })
                c.Padding = new Padding(8, 4, 0, 0);

            list.Columns.Add("Nr", 60, HorizontalAlignment.Right);
            list.Columns.Add("Zeit", 95);
            list.Columns.Add("Befehl", 175);
            list.Columns.Add("A", 25);
            list.Columns.Add("Hash", 50);
            list.Columns.Add("DLC", 35, HorizontalAlignment.Right);
            list.Columns.Add("Daten", 200);
            list.Columns.Add("ASCII", 75);
            list.Columns.Add("Deutung", 400);
            list.RetrieveVirtualItem += List_RetrieveVirtualItem;

            statusStrip.Items.Add(status);
            Controls.Add(list);
            Controls.Add(bar);
            Controls.Add(statusStrip);

            btnConnect.Click += (s, e) => Connect();
            btnPause.Click += (s, e) => TogglePause();
            btnMarker.Click += (s, e) => AddMarker();
            btnClear.Click += (s, e) => ClearAll();
            btnSave.Click += (s, e) => Save();
            chkHidePing.CheckedChanged += (s, e) => RebuildVisible();
            drainTimer.Tick += (s, e) => Drain();
            FormClosing += OnFormClosing;

            UpdateStatus("Bitte die CANguru-GUI schließen und dann »Verbinden« klicken.");
        }

        // ------------------------------------------------------------
        // Connection
        // ------------------------------------------------------------

        private void Connect()
        {
            try
            {
                receiver = new UdpClient(ProtocolConstants.GUI_PORT);
            }
            catch (SocketException)
            {
                MessageBox.Show(this,
                    $"Der Port {ProtocolConstants.GUI_PORT} ist belegt. Läuft noch die CANguru-GUI?\r\n\r\n" +
                    "Der Sniffer empfängt auf demselben Port wie die GUI. Bitte die GUI beenden und erneut versuchen.",
                    "CANguru-Sniffer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            sender = new UdpClient { EnableBroadcast = true };
            receiver.BeginReceive(OnUdpData, null);

            btnConnect.Enabled = false;
            btnConnect.Text = "Suche Bridge …";
            btnPause.Enabled = true;
            startTime = DateTime.Now;
            drainTimer.Start();

            SendDiscover();
            discoverTimer = new System.Threading.Timer(_ => SendDiscover(), null, 1000, 1000);
            UpdateStatus("Suche die Bridge im Netzwerk …");
        }

        // SESSION DISCOVER as broadcast, once per second until the bridge answers
        private void SendDiscover()
        {
            if (bridgeIp != null || closing || sender == null)
                return;
            var h = new Cg2Header
            {
                Feature = (byte)ProtocolConstants.CG2_F_SESSION,
                Command = (byte)ProtocolConstants.CG2_SESSION_DISCOVER,
                Flags = 0,
                Seq = 0,
                Decoder = (byte)ProtocolConstants.CG2_ID_BRIDGE
            };
            byte[] packet = Cg2.Encode(h, new byte[] { (byte)ProtocolConstants.CG2_PROTO_MINOR });
            try
            {
                sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, ProtocolConstants.BRIDGE_PORT));
            }
            catch (SocketException)
            {
                // network not ready yet - next attempt in a second
            }
        }

        private void OnUdpData(IAsyncResult ar)
        {
            if (closing || receiver == null)
                return;
            var remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] data;
            try
            {
                data = receiver.EndReceive(ar, ref remote!);
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { data = Array.Empty<byte>(); }

            try
            {
                if (data.Length > 0)
                    Process(data, remote);
            }
            catch
            {
                // a broken packet must not end the program
            }

            try { receiver.BeginReceive(OnUdpData, null); } catch { }
        }

        private void Process(byte[] data, IPEndPoint remote)
        {
            if (!Cg2.IsV2(data) || Cg2.Decode(data, out var h, out var payload) != Cg2DecodeResult.Ok)
                return;

            if (h.Feature == ProtocolConstants.CG2_F_SESSION && h.Command == ProtocolConstants.CG2_SESSION_DISCOVER_REPLY)
            {
                if (bridgeIp != null)
                    return;
                bridgeIp = remote.Address;
                discoverTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                string fw = payload.Length >= 2 ? $"{payload[0]}.{payload[1]}" : "?";
                bool gleisbox = payload.Length >= 4 && payload[3] == 1;
                BeginInvoke(() =>
                {
                    lblBridge.Text = $"Bridge {bridgeIp}, Firmware {fw}" + (gleisbox ? ", Gleisbox erkannt" : "");
                    btnConnect.Text = "Verbunden";
                    UpdateStatus("Verbunden – der CAN-Verkehr wird aufgezeichnet.");
                });
                return;
            }

            // CAN frame from the bus: [seq u32][frame 13][source]
            if (h.Feature == ProtocolConstants.CG2_F_CAN && h.Command == ProtocolConstants.CG2_CAN_TO_GUI &&
                payload.Length >= 4 + ProtocolConstants.CAN_FRAME_SIZE)
            {
                if (paused)
                    return;
                var frame = new MaerklinFrame(payload[4..(4 + ProtocolConstants.CAN_FRAME_SIZE)]);
                incoming.Enqueue(new Entry
                {
                    Time = DateTime.Now,
                    Seq = BitConverter.ToUInt32(payload, 0),
                    Frame = frame,
                    Meaning = frame.Meaning()
                });
            }
            // everything else (decoder lists, alive summaries ...) is not of interest here
        }

        // ------------------------------------------------------------
        // List
        // ------------------------------------------------------------

        // Takes the received frames into the list (UI thread, every 100 ms)
        private void Drain()
        {
            bool added = false;
            while (incoming.TryDequeue(out var e))
            {
                // the bridge numbers the frames; a gap means frames were lost on the way
                if (lastSeq.HasValue && e.Seq != unchecked(lastSeq.Value + 1) && e.Seq > lastSeq.Value)
                {
                    int lost = (int)(e.Seq - lastSeq.Value - 1);
                    lostFrames += lost;
                    AddEntry(new Entry { Time = e.Time, Text = $"*** {lost} Frame(s) verloren ***" });
                }
                lastSeq = e.Seq;
                AddEntry(e);
                added = true;
                unsaved = true;
            }
            if (added)
                FlushList();
        }

        private void AddEntry(Entry e)
        {
            e.Nr = nextNr++;
            entries.Add(e);
            if (IsVisible(e))
                visible.Add(entries.Count - 1);
        }

        private bool IsVisible(Entry e) =>
            !(chkHidePing.Checked && e.Frame != null && e.Frame.Command == 0x18);

        private void FlushList()
        {
            list.VirtualListSize = visible.Count;
            if (chkAutoScroll.Checked && visible.Count > 0)
                list.EnsureVisible(visible.Count - 1);
            UpdateStatus(null);
        }

        private void RebuildVisible()
        {
            visible.Clear();
            for (int i = 0; i < entries.Count; i++)
                if (IsVisible(entries[i]))
                    visible.Add(i);
            list.VirtualListSize = visible.Count;
            list.Invalidate();
            UpdateStatus(null);
        }

        private void List_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
        {
            var en = entries[visible[e.ItemIndex]];
            string time = (en.Time - startTime).ToString(@"mm\:ss\.fff");
            ListViewItem item;
            if (en.Frame == null)
            {
                item = new ListViewItem(new[] { en.Nr.ToString(), time, en.Text, "", "", "", "", "", "" })
                {
                    BackColor = Color.LightYellow,
                    Font = new Font(list.Font, FontStyle.Bold)
                };
            }
            else
            {
                var f = en.Frame;
                item = new ListViewItem(new[]
                {
                    en.Nr.ToString(), time, f.CommandName, f.IsResponse ? "A" : "",
                    f.Hash.ToString("X4"), f.Dlc.ToString(), f.DataHex(), f.DataAscii(), en.Meaning
                });
                // answers in grey, the mfx registration commands highlighted
                if (f.Command is 0x01 or 0x02 or 0x03)
                    item.BackColor = Color.FromArgb(225, 240, 255);
                else if (f.Command is 0x07 or 0x08 or 0x1D or 0x20 or 0x21)
                    item.BackColor = Color.FromArgb(235, 250, 235);
                if (f.IsResponse)
                    item.ForeColor = Color.DimGray;
            }
            e.Item = item;
        }

        // ------------------------------------------------------------
        // Buttons
        // ------------------------------------------------------------

        private void TogglePause()
        {
            paused = !paused;
            btnPause.Text = paused ? "Weiter" : "Pause";
            AddEntry(new Entry { Time = DateTime.Now, Text = paused ? "--- Aufzeichnung angehalten ---" : "--- Aufzeichnung fortgesetzt ---" });
            lastSeq = null; // frames dropped during the pause are not "lost"
            FlushList();
        }

        private void AddMarker()
        {
            string? text = AskText("Markierung", "Text der Markierung (z. B. »Lok aufs Programmiergleis gestellt«):");
            if (text == null)
                return;
            AddEntry(new Entry { Time = DateTime.Now, Text = "### " + (text.Length > 0 ? text : "Markierung") });
            FlushList();
        }

        private void ClearAll()
        {
            if (entries.Count > 0 && MessageBox.Show(this, "Alle aufgezeichneten Frames löschen?", "Leeren",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            entries.Clear();
            visible.Clear();
            nextNr = 1;
            lostFrames = 0;
            lastSeq = null;
            unsaved = false;
            startTime = DateTime.Now;
            list.VirtualListSize = 0;
            list.Invalidate();
            UpdateStatus(null);
        }

        // Saves all frames (also hidden pings) as tab-separated text
        private void Save()
        {
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "Es ist noch nichts aufgezeichnet.", "Speichern");
                return;
            }
            using var dlg = new SaveFileDialog
            {
                Filter = "Textdatei (*.txt)|*.txt|Alle Dateien (*.*)|*.*",
                FileName = $"CAN-Mitschnitt_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            var sb = new StringBuilder();
            sb.AppendLine($"# CANguru-Sniffer – CAN-Mitschnitt vom {startTime:dd.MM.yyyy HH:mm:ss}");
            sb.AppendLine($"# {lblBridge.Text}");
            sb.AppendLine($"# {entries.Count(e => e.Frame != null)} Frames, {lostFrames} verloren");
            sb.AppendLine("# Zeit relativ zum Beginn der Aufzeichnung (Empfangszeit am PC). Roh = 13 Byte wie von der Bridge: CAN-ID (4), DLC, Daten (8)");
            sb.AppendLine("Nr\tZeit\tSeq\tRoh\tBefehl\tAntwort\tHash\tDLC\tDaten\tASCII\tDeutung");
            foreach (var e in entries)
            {
                string time = (e.Time - startTime).ToString(@"mm\:ss\.fff");
                if (e.Frame == null)
                {
                    sb.AppendLine($"{e.Nr}\t{time}\t\t\t{e.Text}");
                    continue;
                }
                var f = e.Frame;
                sb.AppendLine(string.Join('\t', e.Nr, time, e.Seq, f.RawHex(), f.CommandName, f.IsResponse ? "A" : "",
                    f.Hash.ToString("X4"), f.Dlc, f.DataHex(), f.DataAscii(), e.Meaning));
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            unsaved = false;
            UpdateStatus($"Gespeichert: {dlg.FileName}");
        }

        private void UpdateStatus(string? text)
        {
            int frames = entries.Count(e => e.Frame != null);
            string counts = $"{frames} Frames" + (lostFrames > 0 ? $", {lostFrames} verloren" : "") +
                            (visible.Count != entries.Count ? $", {visible.Count} angezeigt" : "");
            status.Text = text != null ? $"{text}   ({counts})" : counts + (paused ? " – angehalten" : "");
        }

        private string? AskText(string title, string prompt)
        {
            using var f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(440, 110)
            };
            var lbl = new Label { Text = prompt, Location = new Point(10, 10), AutoSize = true };
            var box = new TextBox { Location = new Point(10, 35), Width = 420 };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(270, 72) };
            var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, Location = new Point(355, 72) };
            f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            return f.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
        }

        private void OnFormClosing(object? s, FormClosingEventArgs e)
        {
            if (unsaved && MessageBox.Show(this, "Der Mitschnitt ist noch nicht gespeichert. Trotzdem beenden?",
                    "CANguru-Sniffer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            // deliberately no SESSION END: the bridge keeps running
            closing = true;
            drainTimer.Stop();
            discoverTimer?.Dispose();
            receiver?.Dispose();
            sender?.Dispose();
        }
    }
}
