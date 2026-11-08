using CANguru.Controllers;
using CANguru.Models;
using CANguru.Networking;
using CANguru.Protocol;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CANguru.GUI.Forms
{
    // Settings window of a decoder, built entirely from its self-description
    // (parameters, actions, measured values; PROTOKOLL.md 5.4). No code per decoder type.
    //
    // Layout:
    //  - "Einstellungen": parameters without group
    //  - one frame per group (name rule "Gruppe: Name", e.g. "Signal 3: Adresse"),
    //    frames side by side; a frame holds the group's parameters, actions and values
    //  - "Aktionen" and "Messwerte" without group
    // Actions are buttons labelled with their name. Measured values are shown as text
    // (choice) or dots (bit mask). Changed, unsaved values are marked.
    //
    // The window appears at once and fills while loading. The description is cached
    // (DescriptionCache), so from the second opening on only the values are read.
    public sealed class ParameterForm : Form
    {
        private const int WideLabel = 250, WideControl = 190, WideUnit = 70;  // sections across the window
        private const int TileLabel = 110, TileControl = 110, TileUnit = 40;  // group frames side by side

        private readonly CANguruDecoder decoder;
        private byte DecoderId => (byte)decoder.AssignedId;

        private readonly FlowLayoutPanel content = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8)
        };
        private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(10, 4, 10, 0) };
        private readonly Button btnSave = new() { Text = "Speichern", AutoSize = true, Enabled = false };
        private readonly Button btnDiscard = new() { Text = "Verwerfen", AutoSize = true, Enabled = false };
        private readonly Button btnDefaults = new() { Text = "Werkseinstellungen", AutoSize = true, Enabled = false };
        private readonly Button btnClose = new() { Text = "Schließen", AutoSize = true, DialogResult = DialogResult.Cancel };
        private readonly ToolTip toolTip = new();

        private sealed class ParamRow
        {
            public Cg2Description Desc = null!;
            public Control Control = null!;
            public Label Caption = null!;
            public ushort Loaded;
            public bool HasValue;
            public bool Dirty => HasValue && ControlValue(Control) != Loaded;
        }

        private readonly List<ParamRow> paramRows = new();
        private readonly Dictionary<int, (Cg2Description desc, Control view)> valueViews = new();
        private FlowLayoutPanel? tilePanel;
        private bool loading = true;
        private bool busy;

        public ParameterForm(CANguruDecoder decoder)
        {
            this.decoder = decoder;
            Text = $"Einstellungen – {decoder.DisplayName} (ID {decoder.AssignedId}, Firmware {decoder.Firmware})";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(660, 720);
            MinimumSize = new Size(480, 320);
            CancelButton = btnClose;

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(10, 6, 10, 6)
            };
            buttons.Controls.AddRange(new Control[] { btnClose, btnSave, btnDiscard, btnDefaults });

            Controls.Add(content);
            Controls.Add(status);
            Controls.Add(buttons);

            btnSave.Click += async (s, e) => await SaveAsync();
            btnDiscard.Click += (s, e) => Discard();
            btnDefaults.Click += async (s, e) => await FactoryResetAsync();
            Shown += async (s, e) => await LoadAsync();
            FormClosing += OnFormClosing;
            content.Resize += (s, e) => FitTilePanel();
        }

        // ------------------------------------------------------------
        // Loading
        // ------------------------------------------------------------

        private async Task LoadAsync()
        {
            try
            {
                var desc = DescriptionCache.Get(decoder);
                if (desc == null)
                {
                    desc = await DescribeAsync();
                    if (desc == null)
                    {
                        status.Text = "Der Decoder antwortet nicht.";
                        return;
                    }
                    DescriptionCache.Put(decoder, desc);
                }

                Build(desc);

                for (int i = 0; i < paramRows.Count; i++)
                {
                    status.Text = $"Lese Werte {i + 1} von {paramRows.Count} ...";
                    var row = paramRows[i];
                    ushort? v = await ReadAsync(row.Desc.Number);
                    if (v == null)
                    {
                        status.Text = $"Wert „{row.Desc.Name}“ nicht lesbar.";
                        return;
                    }
                    SetLoaded(row, v.Value, overwrite: true);
                    row.Control.Enabled = true;
                }

                status.Text = desc.Params.Count + desc.Actions.Count + desc.Values.Count == 0
                    ? "Dieser Decoder beschreibt (noch) keine Parameter."
                    : "";
                loading = false;
                UpdateButtons();
            }
            catch (Exception ex)
            {
                status.Text = "Fehler: " + ex.Message;
            }
        }

        // Whole description; null = no answer
        private async Task<DecoderDescription?> DescribeAsync()
        {
            var parameters = await DescribeAllAsync((byte)ProtocolConstants.CG2_KIND_PARAM, "Einstellungen");
            if (parameters == null) return null;
            var actions = await DescribeAllAsync((byte)ProtocolConstants.CG2_KIND_ACTION, "Aktionen");
            if (actions == null) return null;
            var values = await DescribeAllAsync((byte)ProtocolConstants.CG2_KIND_VALUE, "Messwerte");
            if (values == null) return null;
            return new DecoderDescription { Params = parameters, Actions = actions, Values = values };
        }

        // All entries of one kind; null = no answer
        private async Task<List<Cg2Description>?> DescribeAllAsync(byte kind, string what)
        {
            var list = new List<Cg2Description>();
            int count = 1;
            for (int n = 0; n < count; n++)
            {
                status.Text = $"Lese die Beschreibung des Decoders: {what} {n + 1}" + (n > 0 ? $" von {count}" : "") + " ...";
                var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_PARAM,
                    (byte)ProtocolConstants.CG2_PARAM_DESCRIBE_GET, DecoderId, new[] { kind, (byte)n });
                if (r == null)
                    return null;
                if (r.IsError)
                {
                    // number 0 does not exist: the decoder has no entries of this kind
                    if (n == 0 && r.ErrorCode == ProtocolConstants.CG2_ERR_BAD_VALUE)
                        return list;
                    // older firmware without the parameter feature
                    if (n == 0 && r.ErrorCode == ProtocolConstants.CG2_ERR_UNKNOWN_COMMAND)
                        return list;
                    throw new InvalidOperationException(BridgeLink.ErrorText(r.ErrorCode));
                }
                var d = Cg2Description.Parse(r.Payload, out count);
                if (d == null)
                    throw new InvalidOperationException("Beschreibung fehlerhaft.");
                list.Add(d);
            }
            return list;
        }

        private async Task<ushort?> ReadAsync(byte number)
        {
            var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_PARAM,
                (byte)ProtocolConstants.CG2_PARAM_READ, DecoderId, new[] { number });
            if (r == null || r.IsError || r.Payload.Length < 3)
                return null;
            return new Cg2Reader(r.Payload[1..]).U16();
        }

        // ------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------

        private void Build(DecoderDescription desc)
        {
            content.SuspendLayout();

            var looseParams = desc.Params.Where(d => d.Group == "").ToList();
            var looseActions = desc.Actions.Where(d => d.Group == "").ToList();
            var looseValues = desc.Values.Where(d => d.Group == "").ToList();
            var groups = desc.Params.Concat(desc.Actions).Concat(desc.Values)
                             .Select(d => d.Group).Where(g => g != "").Distinct().ToList();

            // order: settings, measured values and actions first (always visible without
            // scrolling), then the group frames (can be many, e.g. 16 contacts)
            if (looseParams.Count > 0)
                content.Controls.Add(MakeFrame("Einstellungen", looseParams, wide: true));
            if (looseValues.Count > 0)
                content.Controls.Add(MakeFrame("Messwerte", looseValues, wide: true));
            if (looseActions.Count > 0)
                content.Controls.Add(MakeFrame("Aktionen", looseActions, wide: true));

            if (groups.Count > 0)
            {
                tilePanel = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(0)
                };
                foreach (var g in groups)
                {
                    var items = desc.Params.Where(d => d.Group == g)
                        .Concat(desc.Actions.Where(d => d.Group == g))
                        .Concat(desc.Values.Where(d => d.Group == g)).ToList();
                    tilePanel.Controls.Add(MakeFrame(g, items, wide: false));
                }
                content.Controls.Add(tilePanel);
            }

            content.ResumeLayout();
            FitTilePanel();
        }

        // Group frames wrap at the width of the window
        private void FitTilePanel()
        {
            if (tilePanel != null)
                tilePanel.MaximumSize = new Size(Math.Max(200, content.ClientSize.Width - content.Padding.Horizontal - 4), 0);
        }

        private GroupBox MakeFrame(string title, List<Cg2Description> items, bool wide)
        {
            int wl = wide ? WideLabel : TileLabel, wc = wide ? WideControl : TileControl, wu = wide ? WideUnit : TileUnit;
            var table = new TableLayoutPanel
            {
                ColumnCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(4, 2, 4, 4)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, wl));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, wc));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, wu));

            foreach (var d in items)
            {
                if (d.Kind == ProtocolConstants.CG2_KIND_PARAM)
                    AddParam(table, d);
                else if (d.Kind == ProtocolConstants.CG2_KIND_ACTION)
                    AddAction(table, d);
                else
                    AddValue(table, d);
            }

            var box = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6, 4, 6, 4),
                Margin = new Padding(0, 0, 8, 8)
            };
            box.Controls.Add(table);
            return box;
        }

        private static Label Caption(string text) =>
            new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };

        private static Label Unit(string text) =>
            new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 6, 0, 0) };

        private void AddParam(TableLayoutPanel table, Cg2Description d)
        {
            Control c;
            if (d.Type == ProtocolConstants.CG2_TYPE_BOOL)
                c = new CheckBox { AutoSize = true };
            else if (d.Type == ProtocolConstants.CG2_TYPE_CHOICE)
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                cb.Items.AddRange(d.Choices);
                c = cb;
            }
            else
            {
                c = new NumericUpDown
                {
                    Minimum = d.Min,
                    Maximum = Math.Max(d.Min, d.Max),
                    Increment = Math.Max((ushort)1, d.Step),
                    ThousandsSeparator = true
                };
            }
            c.Enabled = false; // until the value is read
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            toolTip.SetToolTip(c, $"Standard: {d.FormatValue(d.Default)}" +
                                  (d.Type <= ProtocolConstants.CG2_TYPE_U16 ? $", Bereich {d.Min}–{d.Max}" : ""));

            var row = new ParamRow { Desc = d, Control = c, Caption = Caption(d.Label) };
            paramRows.Add(row);
            switch (c)
            {
                case CheckBox cb: cb.CheckedChanged += (s, e) => MarkDirty(row); break;
                case ComboBox co: co.SelectedIndexChanged += (s, e) => MarkDirty(row); break;
                case NumericUpDown n: n.ValueChanged += (s, e) => MarkDirty(row); break;
            }

            int r = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(row.Caption, 0, r);
            table.Controls.Add(c, 1, r);
            table.Controls.Add(Unit(d.Unit), 2, r);
        }

        private void AddAction(TableLayoutPanel table, Cg2Description d)
        {
            var b = new Button { Text = d.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 2) };
            if (d.Group != "")
                toolTip.SetToolTip(b, d.Name);
            b.Click += async (s, e) => await ExecuteAsync(d, b);

            int r = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(b, 0, r);
            table.SetColumnSpan(b, 3);
        }

        private void AddValue(TableLayoutPanel table, Cg2Description d)
        {
            int r = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(Caption(d.Label), 0, r);

            if (d.Type == ProtocolConstants.CG2_TYPE_BITS)
            {
                // dots in their own row below the caption, across the whole frame
                var dots = new BitDots(d.Choices) { Anchor = AnchorStyles.Left };
                valueViews[d.Number] = (d, dots);
                r = table.RowCount++;
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                table.Controls.Add(dots, 0, r);
                table.SetColumnSpan(dots, 3);
                return;
            }

            var l = new Label
            {
                Text = "–",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 0, 0),
                Font = new Font(Font, FontStyle.Bold)
            };
            valueViews[d.Number] = (d, l);
            table.Controls.Add(l, 1, r);
            table.Controls.Add(Unit(d.Unit), 2, r);
        }

        // ------------------------------------------------------------
        // Values and change marks
        // ------------------------------------------------------------

        private static ushort ControlValue(Control c) => c switch
        {
            CheckBox cb => (ushort)(cb.Checked ? 1 : 0),
            ComboBox co => (ushort)Math.Max(0, co.SelectedIndex),
            NumericUpDown n => (ushort)n.Value,
            _ => 0
        };

        private static void SetControlValue(Control c, ushort v)
        {
            switch (c)
            {
                case CheckBox cb: cb.Checked = v != 0; break;
                case ComboBox co when v < co.Items.Count: co.SelectedIndex = v; break;
                case NumericUpDown n: n.Value = Math.Clamp(v, n.Minimum, n.Maximum); break;
            }
        }

        // New value from the decoder. overwrite = false keeps unsaved input of the user.
        private void SetLoaded(ParamRow row, ushort v, bool overwrite)
        {
            bool keepInput = !overwrite && row.Dirty;
            row.Loaded = v;
            row.HasValue = true;
            if (!keepInput)
                SetControlValue(row.Control, v);
            MarkDirty(row);
        }

        private void MarkDirty(ParamRow row)
        {
            bool dirty = row.Dirty;
            row.Caption.Font = new Font(Font, dirty ? FontStyle.Bold : FontStyle.Regular);
            row.Caption.ForeColor = dirty ? Color.DarkOrange : SystemColors.ControlText;
            UpdateButtons();
        }

        private int DirtyCount => paramRows.Count(r => r.Dirty);

        private void UpdateButtons()
        {
            bool idle = !loading && !busy;
            int dirty = DirtyCount;
            btnSave.Enabled = idle && dirty > 0;
            btnDiscard.Enabled = idle && dirty > 0;
            btnDefaults.Enabled = idle && paramRows.Count > 0;
            if (idle && dirty > 0)
                status.Text = dirty == 1 ? "1 Änderung nicht gespeichert." : $"{dirty} Änderungen nicht gespeichert.";
        }

        private void Discard()
        {
            foreach (var row in paramRows.Where(r => r.HasValue))
                SetControlValue(row.Control, row.Loaded);
            status.Text = "Änderungen verworfen.";
            UpdateButtons();
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (DirtyCount == 0)
                return;
            if (MessageBox.Show(this, "Es gibt nicht gespeicherte Änderungen. Fenster trotzdem schließen?",
                    "Einstellungen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                e.Cancel = true;
        }

        // ------------------------------------------------------------
        // Save, factory reset, actions
        // ------------------------------------------------------------

        private async Task SaveAsync()
        {
            SetBusy(true);
            int saved = 0;
            var hints = new List<string>();
            try
            {
                foreach (var row in paramRows.Where(r => r.Dirty).ToList())
                {
                    var d = row.Desc;
                    ushort wanted = ControlValue(row.Control);
                    var p = new Cg2Writer().U8(d.Number).U16(wanted).ToArray();
                    var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_PARAM,
                        (byte)ProtocolConstants.CG2_PARAM_WRITE, DecoderId, p);
                    if (r == null || r.IsError || r.Payload.Length < 3)
                    {
                        status.Text = $"„{d.Name}“ nicht gespeichert: " +
                                      (r == null ? "keine Antwort." : BridgeLink.ErrorText(r.ErrorCode));
                        return;
                    }
                    ushort stored = new Cg2Reader(r.Payload[1..]).U16();
                    SetLoaded(row, stored, overwrite: true);
                    saved++;
                    if (stored != wanted)
                        hints.Add($"„{d.Name}“ auf {d.FormatValue(stored)} {d.Unit}".TrimEnd() + " begrenzt");
                }
                // a setting can change the description itself (e.g. shunt -> maximum threshold)
                if (saved > 0)
                    DescriptionCache.Invalidate(decoder);
                status.Text = saved == 0 ? "Nichts geändert."
                            : $"{saved} Wert(e) gespeichert." + (hints.Count > 0 ? " " + string.Join(", ", hints) + "." : "");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task FactoryResetAsync()
        {
            if (MessageBox.Show(this, "Alle Einstellungen dieses Decoders auf die Standardwerte zurücksetzen? " +
                                      "Adressen und Hardware-Einstellungen bleiben erhalten.",
                    "Werkseinstellungen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            SetBusy(true);
            try
            {
                var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_PARAM,
                    (byte)ProtocolConstants.CG2_PARAM_FACTORY_RESET, DecoderId, null, timeoutMs: 1500);
                if (r == null || r.IsError)
                {
                    status.Text = "Werkseinstellungen nicht übernommen: " +
                                  (r == null ? "keine Antwort." : BridgeLink.ErrorText(r.ErrorCode));
                    return;
                }
                DescriptionCache.Invalidate(decoder);
                // read again, discarding input: the defaults apply now
                foreach (var row in paramRows)
                {
                    ushort? v = await ReadAsync(row.Desc.Number);
                    if (v != null)
                        SetLoaded(row, v.Value, overwrite: true);
                }
                status.Text = "Standardwerte geladen.";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task ExecuteAsync(Cg2Description d, Button b)
        {
            b.Enabled = false;
            try
            {
                var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_ACTION,
                    (byte)ProtocolConstants.CG2_ACTION_EXECUTE, DecoderId, new[] { d.Number });
                status.Text = r == null ? $"„{d.Name}“: keine Antwort."
                            : r.IsError ? $"„{d.Name}“: {BridgeLink.ErrorText(r.ErrorCode)}."
                            : $"„{d.Name}“ ausgeführt.";
            }
            finally
            {
                b.Enabled = true;
            }
        }

        private void SetBusy(bool on)
        {
            busy = on;
            UseWaitCursor = on;
            UpdateButtons();
        }

        // ------------------------------------------------------------
        // Events of the decoder (called from the receive thread)
        // ------------------------------------------------------------

        public void OnEvent(byte fromDecoder, byte ev, byte[] data)
        {
            if (fromDecoder != DecoderId || IsDisposed || !IsHandleCreated)
                return;
            BeginInvoke(async () =>
            {
                status.Text = ev switch
                {
                    (byte)ProtocolConstants.CG2_EV_POSITION => "Stellung erreicht.",
                    (byte)ProtocolConstants.CG2_EV_CALIBRATED => "Kalibrierung gespeichert.",
                    (byte)ProtocolConstants.CG2_EV_OVERCURRENT => "Überstrom – abgeschaltet.",
                    _ => $"Ereignis {ev}."
                };
                // calibration stores the travel: read the parameters again
                if (ev == ProtocolConstants.CG2_EV_CALIBRATED && !loading)
                    await ReloadParamsAsync();
            });
        }

        private async Task ReloadParamsAsync()
        {
            foreach (var row in paramRows)
            {
                ushort? v = await ReadAsync(row.Desc.Number);
                if (v != null)
                    SetLoaded(row, v.Value, overwrite: false);
            }
        }

        // ------------------------------------------------------------
        // Measured values from the alive (called from the receive thread)
        // ------------------------------------------------------------

        public void UpdateValues(byte fromDecoder, int[] values)
        {
            if (fromDecoder != DecoderId || IsDisposed || !IsHandleCreated)
                return;
            BeginInvoke(() =>
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (!valueViews.TryGetValue(i, out var v))
                        continue;
                    switch (v.view)
                    {
                        case BitDots dots: dots.Value = values[i]; break;
                        case Label l: l.Text = v.desc.FormatValue(values[i]); break;
                    }
                }
            });
        }
    }
}
