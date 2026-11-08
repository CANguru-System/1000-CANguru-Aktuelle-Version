using CANguru.Protocol;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace CANguru.GUI.Forms.DecoderViews
{
    // Power decoder: one current bar per channel with a mark at the threshold;
    // below: threshold, shunt and measuring range.
    // Uses: values "Strom Kanal k" (mA, 0xFFFF = not connected), parameters
    // "Abschaltgrenze" (its maximum is the measuring range) and "Shunt-Widerstand".
    public sealed class PowerView : DecoderView
    {
        private readonly List<(Cg2Description desc, CurrentBar bar)> bars = new();
        private readonly Cg2Description? threshold, shunt;
        private readonly Label lblInfo = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 0) };

        public static PowerView? TryCreate(IDecoderViewHost host)
        {
            bool ok = host.Description.Values.Any(v => v.Name.StartsWith("Strom Kanal"));
            return ok ? new PowerView(host) : null;
        }

        private PowerView(IDecoderViewHost host) : base(host)
        {
            threshold = Param("Abschaltgrenze");
            shunt = Param("Shunt-Widerstand");
            int range = threshold != null && threshold.Max > 0 ? threshold.Max : 2000;

            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            foreach (var v in host.Description.Values.Where(v => v.Name.StartsWith("Strom Kanal")))
            {
                var bar = new CurrentBar { Range = range, Unit = v.Unit };
                bars.Add((v, bar));
                int r = table.RowCount++;
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                table.Controls.Add(new Label { Text = v.Name.Replace("Strom ", ""), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, r);
                table.Controls.Add(bar, 1, r);
            }

            var all = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            all.Controls.Add(table);
            all.Controls.Add(lblInfo);
            Controls.Add(all);
            ShowInfo();
        }

        public override IEnumerable<Cg2Description> Covered => bars.Select(b => b.desc);

        private void ShowInfo()
        {
            var parts = new List<string>();
            ushort? t = threshold != null ? Host.ParamValue(threshold) : null;
            if (t != null)
                parts.Add($"Abschaltgrenze {t:N0} mA");
            ushort? s = shunt != null ? Host.ParamValue(shunt) : null;
            if (s != null)
                parts.Add($"Shunt {shunt!.FormatValue(s.Value)}");
            if (threshold != null && threshold.Max > 0)
                parts.Add($"Messbereich bis {threshold.Max:N0} mA");
            lblInfo.Text = string.Join(" · ", parts) + (shunt != null ? "  (Shunt unten bei den Einstellungen)" : "");
        }

        public override void ParamChanged(Cg2Description param, ushort value)
        {
            if (param == threshold)
                foreach (var b in bars)
                    b.bar.Threshold = value;
            if (param == threshold || param == shunt)
                ShowInfo();
        }

        public override void ValuesChanged(int[] values)
        {
            foreach (var b in bars)
            {
                int v = ValueOf(values, b.desc);
                if (v >= 0)
                    b.bar.Current = v == 0xFFFF ? -1 : v;
            }
        }
    }

    // Horizontal current bar: green below the threshold, red from it on; a dark line
    // marks the threshold. Current -1 = channel not connected.
    internal sealed class CurrentBar : Control
    {
        private int current = -2; // -2 = no value yet
        private int threshold;

        public CurrentBar()
        {
            DoubleBuffered = true;
            Size = new Size(400, 24);
            Margin = new Padding(0, 3, 0, 3);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Range { get; set; } = 2000;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Unit { get; set; } = "mA";

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Current { get => current; set { current = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Threshold { get => threshold; set { threshold = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            g.FillRectangle(Brushes.White, r);
            if (current >= 0 && Range > 0)
            {
                int w = (int)Math.Round(Math.Min(current, Range) * (double)r.Width / Range);
                using var fill = new SolidBrush(threshold > 0 && current >= threshold ? Color.Red : Color.LimeGreen);
                g.FillRectangle(fill, 1, 1, w, r.Height - 1);
            }
            if (threshold > 0 && Range > 0)
            {
                int x = (int)Math.Round(Math.Min(threshold, Range) * (double)r.Width / Range);
                using var mark = new Pen(Color.FromArgb(160, 0, 0), 2);
                g.DrawLine(mark, x, 0, x, Height);
            }
            g.DrawRectangle(Pens.Gray, r);
            string text = current == -2 ? "–" : current < 0 ? "nicht angeschlossen" : $"{current:N0} {Unit}";
            TextRenderer.DrawText(g, text, Font, r, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
