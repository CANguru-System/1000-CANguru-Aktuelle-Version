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
    // Eintrag aus der Selbstbeschreibung eines Decoders (PROTOKOLL.md 5.4)
    public sealed class Cg2Description
    {
        public byte Kind { get; init; }   // CG2_KIND_PARAM, _ACTION, _VALUE
        public byte Number { get; init; }
        public byte Type { get; init; }   // CG2_TYPE_U8, _U16, _CHOICE, _BOOL
        public ushort Min { get; init; }
        public ushort Max { get; init; }
        public ushort Default { get; init; }
        public ushort Step { get; init; }
        public string Unit { get; init; } = "";
        public string Name { get; init; } = "";
        public string[] Choices { get; init; } = Array.Empty<string>();
    }

    // Allgemeines Einstellungsfenster: baut sich aus der Selbstbeschreibung des Decoders
    // auf (Parameter, Aktionen, Messwerte). Kein eigener Code je Decodertyp nötig.
    public sealed class ParameterForm : Form
    {
        private readonly byte decoderId;
        private readonly string title;

        private readonly TableLayoutPanel table = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 3,
            Padding = new Padding(10)
        };
        private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(10, 4, 10, 0) };
        private readonly Button btnSave = new() { Text = "Speichern", AutoSize = true, Enabled = false };
        private readonly Button btnDefaults = new() { Text = "Werkseinstellungen", AutoSize = true, Enabled = false };
        private readonly Button btnClose = new() { Text = "Schließen", AutoSize = true, DialogResult = DialogResult.Cancel };

        private readonly List<(Cg2Description desc, Control control, ushort loaded)> paramControls = new();
        private readonly List<(Cg2Description desc, Label label)> valueLabels = new();

        public ParameterForm(byte decoderId, string title)
        {
            this.decoderId = decoderId;
            this.title = title;
            Text = $"Parameter – {title} (ID {decoderId})";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(520, 420);
            MinimumSize = new Size(420, 300);
            CancelButton = btnClose;

            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(10, 6, 10, 6)
            };
            buttons.Controls.AddRange(new Control[] { btnClose, btnSave, btnDefaults });

            Controls.Add(table);
            Controls.Add(status);
            Controls.Add(buttons);

            btnSave.Click += async (s, e) => await SaveAsync();
            btnDefaults.Click += async (s, e) => await FactoryResetAsync();
            Shown += async (s, e) => await LoadAsync();
        }

        // ------------------------------------------------------------
        // Laden
        // ------------------------------------------------------------

        private async Task LoadAsync()
        {
            status.Text = "Lese die Beschreibung des Decoders ...";
            try
            {
                var parameters = await DescribeAllAsync((byte)ProtocolConstants.CG2_KIND_PARAM);
                var actions = await DescribeAllAsync((byte)ProtocolConstants.CG2_KIND_ACTION);
                var values = await DescribeAllAsync((byte)ProtocolConstants.CG2_KIND_VALUE);
                if (parameters == null || actions == null || values == null)
                {
                    status.Text = "Der Decoder antwortet nicht.";
                    return;
                }

                table.SuspendLayout();
                if (parameters.Count > 0)
                {
                    AddHeader("Einstellungen");
                    foreach (var d in parameters)
                    {
                        ushort? v = await ReadAsync(d.Number);
                        if (v == null)
                        {
                            status.Text = $"Wert „{d.Name}“ nicht lesbar.";
                            return;
                        }
                        AddParam(d, v.Value);
                    }
                }
                if (actions.Count > 0)
                {
                    AddHeader("Aktionen");
                    foreach (var d in actions)
                        AddAction(d);
                }
                if (values.Count > 0)
                {
                    AddHeader("Messwerte");
                    foreach (var d in values)
                        AddValue(d);
                }
                // letzte Zeile füllt den Rest, damit die Zeilen oben bündig stehen
                table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                table.Controls.Add(new Label(), 0, table.RowCount++);
                table.ResumeLayout();

                if (parameters.Count + actions.Count + values.Count == 0)
                    status.Text = "Dieser Decoder beschreibt (noch) keine Parameter.";
                else
                    status.Text = "";
                btnSave.Enabled = parameters.Count > 0;
                btnDefaults.Enabled = true;
            }
            catch (Exception ex)
            {
                status.Text = "Fehler: " + ex.Message;
            }
        }

        // Alle Einträge einer Art; null = keine Antwort
        private async Task<List<Cg2Description>?> DescribeAllAsync(byte kind)
        {
            var list = new List<Cg2Description>();
            int count = 1;
            for (int n = 0; n < count; n++)
            {
                var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_PARAM,
                    (byte)ProtocolConstants.CG2_PARAM_DESCRIBE_GET, decoderId, new[] { kind, (byte)n });
                if (r == null)
                    return null;
                if (r.IsError)
                {
                    // Nummer 0 gibt es nicht: der Decoder hat keine Einträge dieser Art
                    if (n == 0 && r.ErrorCode == ProtocolConstants.CG2_ERR_BAD_VALUE)
                        return list;
                    // ältere Firmware ohne Parameter-Feature
                    if (n == 0 && r.ErrorCode == ProtocolConstants.CG2_ERR_UNKNOWN_COMMAND)
                        return list;
                    throw new InvalidOperationException(BridgeLink.ErrorText(r.ErrorCode));
                }
                var d = Parse(r.Payload, out count);
                if (d == null)
                    throw new InvalidOperationException("Beschreibung fehlerhaft.");
                list.Add(d);
            }
            return list;
        }

        // [art][nummer][anzahl][typ][min][max][standard][schritt][einheit][name][anzahlAuswahl][auswahl]...
        private static Cg2Description? Parse(byte[] payload, out int count)
        {
            var r = new Cg2Reader(payload);
            byte kind = r.U8(), number = r.U8();
            count = r.U8();
            byte type = r.U8();
            ushort min = r.U16(), max = r.U16(), def = r.U16(), step = r.U16();
            string unit = r.Str(), name = r.Str();
            int choiceCount = r.U8();
            var choices = new string[choiceCount];
            for (int i = 0; i < choiceCount; i++)
                choices[i] = r.Str();
            if (!r.Ok)
                return null;
            return new Cg2Description
            {
                Kind = kind, Number = number, Type = type, Min = min, Max = max, Default = def,
                Step = step, Unit = unit, Name = name, Choices = choices
            };
        }

        private async Task<ushort?> ReadAsync(byte number)
        {
            var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_PARAM,
                (byte)ProtocolConstants.CG2_PARAM_READ, decoderId, new[] { number });
            if (r == null || r.IsError || r.Payload.Length < 3)
                return null;
            return new Cg2Reader(r.Payload[1..]).U16();
        }

        // ------------------------------------------------------------
        // Aufbau der Zeilen
        // ------------------------------------------------------------

        private void AddHeader(string text)
        {
            var l = new Label
            {
                Text = text,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, table.RowCount == 0 ? 0 : 12, 0, 4)
            };
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(l, 0, table.RowCount);
            table.SetColumnSpan(l, 3);
            table.RowCount++;
        }

        private void AddRow(string name, Control control, string unit)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, table.RowCount);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            table.Controls.Add(control, 1, table.RowCount);
            table.Controls.Add(new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 6, 0, 0) }, 2, table.RowCount);
            table.RowCount++;
        }

        private void AddParam(Cg2Description d, ushort value)
        {
            Control c;
            if (d.Type == ProtocolConstants.CG2_TYPE_BOOL)
                c = new CheckBox { Checked = value != 0, AutoSize = true };
            else if (d.Type == ProtocolConstants.CG2_TYPE_CHOICE)
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                cb.Items.AddRange(d.Choices);
                if (value < d.Choices.Length)
                    cb.SelectedIndex = value;
                c = cb;
            }
            else
            {
                var nud = new NumericUpDown
                {
                    Minimum = d.Min,
                    Maximum = Math.Max(d.Min, d.Max),
                    Increment = Math.Max((ushort)1, d.Step),
                    ThousandsSeparator = true
                };
                nud.Value = Math.Clamp(value, nud.Minimum, nud.Maximum);
                c = nud;
            }
            new ToolTip().SetToolTip(c, $"Standard: {(d.Type == ProtocolConstants.CG2_TYPE_CHOICE && d.Default < d.Choices.Length ? d.Choices[d.Default] : d.Default.ToString())}" +
                                        (d.Type <= ProtocolConstants.CG2_TYPE_U16 ? $", Bereich {d.Min}–{d.Max}" : ""));
            paramControls.Add((d, c, value));
            AddRow(d.Name, c, d.Unit);
        }

        private void AddAction(Cg2Description d)
        {
            var b = new Button { Text = "Ausführen", AutoSize = true };
            b.Click += async (s, e) => await ExecuteAsync(d, b);
            AddRow(d.Name, b, d.Unit);
        }

        private void AddValue(Cg2Description d)
        {
            var l = new Label { Text = "–", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            valueLabels.Add((d, l));
            AddRow(d.Name, l, d.Unit);
        }

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

        // ------------------------------------------------------------
        // Speichern, Werkseinstellungen, Aktionen
        // ------------------------------------------------------------

        private async Task SaveAsync()
        {
            SetBusy(true);
            int saved = 0;
            var hints = new List<string>();
            try
            {
                for (int i = 0; i < paramControls.Count; i++)
                {
                    var (d, c, loaded) = paramControls[i];
                    ushort wanted = ControlValue(c);
                    if (wanted == loaded)
                        continue;
                    var p = new Cg2Writer().U8(d.Number).U16(wanted).ToArray();
                    var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_PARAM,
                        (byte)ProtocolConstants.CG2_PARAM_WRITE, decoderId, p);
                    if (r == null || r.IsError || r.Payload.Length < 3)
                    {
                        status.Text = $"„{d.Name}“ nicht gespeichert: " +
                                      (r == null ? "keine Antwort." : BridgeLink.ErrorText(r.ErrorCode));
                        return;
                    }
                    ushort stored = new Cg2Reader(r.Payload[1..]).U16();
                    SetControlValue(c, stored);
                    paramControls[i] = (d, c, stored);
                    saved++;
                    if (stored != wanted)
                        hints.Add($"„{d.Name}“ auf {stored} {d.Unit} begrenzt");
                }
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
                                      "Die Zubehöradresse bleibt erhalten.",
                    "Werkseinstellungen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            SetBusy(true);
            try
            {
                var r = await BridgeLink.Instance.RequestAsync((byte)ProtocolConstants.CG2_F_PARAM,
                    (byte)ProtocolConstants.CG2_PARAM_FACTORY_RESET, decoderId, null, timeoutMs: 1500);
                if (r == null || r.IsError)
                {
                    status.Text = "Werkseinstellungen nicht übernommen: " +
                                  (r == null ? "keine Antwort." : BridgeLink.ErrorText(r.ErrorCode));
                    return;
                }
                // Werte neu lesen
                for (int i = 0; i < paramControls.Count; i++)
                {
                    var (d, c, _) = paramControls[i];
                    ushort? v = await ReadAsync(d.Number);
                    if (v == null)
                        continue;
                    SetControlValue(c, v.Value);
                    paramControls[i] = (d, c, v.Value);
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
                    (byte)ProtocolConstants.CG2_ACTION_EXECUTE, decoderId, new[] { d.Number });
                status.Text = r == null ? $"„{d.Name}“: keine Antwort."
                            : r.IsError ? $"„{d.Name}“: {BridgeLink.ErrorText(r.ErrorCode)}."
                            : $"„{d.Name}“ ausgeführt.";
            }
            finally
            {
                b.Enabled = true;
            }
        }

        private void SetBusy(bool busy)
        {
            btnSave.Enabled = !busy && paramControls.Count > 0;
            btnDefaults.Enabled = !busy;
            UseWaitCursor = busy;
        }

        // ------------------------------------------------------------
        // Messwerte aus dem Alive (Aufruf aus dem Empfangsthread)
        // ------------------------------------------------------------

        public void UpdateValues(byte fromDecoder, int[] values)
        {
            if (fromDecoder != decoderId || IsDisposed || !IsHandleCreated)
                return;
            BeginInvoke(() =>
            {
                for (int i = 0; i < valueLabels.Count && i < values.Length; i++)
                    valueLabels[i].label.Text = values[i] == 0xFFFF ? "–" : values[i].ToString("N0");
            });
        }
    }
}
