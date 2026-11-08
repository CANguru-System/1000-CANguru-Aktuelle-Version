using CANguru.Protocol;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CANguru.GUI.Forms.DecoderViews
{
    // LED signal decoder: one small signal per channel (red/green, grey = not present);
    // a click switches it. Brightness and fade time as sliders with immediate effect.
    // Uses: actions "Signal k: umschalten", values "Signal k: Stellung", parameters
    // "Signal k: vorhanden", "Signal k: Adresse", "Helligkeit rot/grün", "Überblendzeit …".
    public sealed class LedSignalView : DecoderView
    {
        private sealed class Channel
        {
            public Cg2Description Toggle = null!, Position = null!;
            public Cg2Description? Present, Address;
            public SignalLamp Lamp = null!;
        }

        private readonly List<Channel> channels = new();
        private readonly List<(Cg2Description desc, TrackBar bar, Label text)> sliders = new();
        private readonly System.Windows.Forms.Timer writeDelay = new() { Interval = 400 };
        private readonly HashSet<TrackBar> pending = new();
        private bool updating; // value set from the decoder, not by the user

        public static LedSignalView? TryCreate(IDecoderViewHost host)
        {
            var d = host.Description;
            bool ok = d.Actions.Any(a => a.Name == "Signal 1: umschalten") && d.Values.Any(v => v.Name == "Signal 1: Stellung");
            return ok ? new LedSignalView(host) : null;
        }

        private LedSignalView(IDecoderViewHost host) : base(host)
        {
            var lamps = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
            for (int k = 1; ; k++)
            {
                var toggle = Action($"Signal {k}: umschalten");
                var pos = Value($"Signal {k}: Stellung");
                if (toggle == null || pos == null)
                    break;
                var ch = new Channel
                {
                    Toggle = toggle,
                    Position = pos,
                    Present = Param($"Signal {k}: vorhanden"),
                    Address = Param($"Signal {k}: Adresse"),
                    Lamp = new SignalLamp { Title = $"Signal {k}" }
                };
                ch.Lamp.Click += async (s, e) =>
                {
                    if (ch.Lamp.Present)
                        await Host.ExecuteAsync(ch.Toggle);
                };
                channels.Add(ch);
                lamps.Controls.Add(ch.Lamp);
            }

            var table = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            foreach (var name in new[] { "Helligkeit rot", "Helligkeit grün" })
                AddSlider(table, Param(name));
            AddSlider(table, Host.Description.Params.FirstOrDefault(p => p.Name.StartsWith("Überblendzeit")));

            var all = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            all.Controls.Add(new Label { Text = "Klick auf ein Signal schaltet es um.", AutoSize = true, ForeColor = Color.DimGray });
            all.Controls.Add(lamps);
            all.Controls.Add(table);
            Controls.Add(all);

            writeDelay.Tick += async (s, e) => await WritePending();
        }

        private void AddSlider(TableLayoutPanel table, Cg2Description? d)
        {
            if (d == null)
                return;
            int step = Math.Max(1, (int)d.Step);
            var bar = new TrackBar
            {
                Minimum = d.Min,
                Maximum = Math.Max(d.Min, d.Max),
                SmallChange = step,
                LargeChange = step * 5,
                TickStyle = TickStyle.None,
                AutoSize = false,
                Height = 28,
                Dock = DockStyle.Fill,
                Enabled = false
            };
            var text = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 6, 0, 0) };
            bar.ValueChanged += (s, e) =>
            {
                // snap to the step width (counted from min), as the decoder does
                int snapped = d.Min + (bar.Value - d.Min + step / 2) / step * step;
                if (snapped != bar.Value && snapped <= bar.Maximum)
                {
                    bar.Value = snapped;
                    return;
                }
                text.Text = $"{bar.Value:N0} {d.Unit}".TrimEnd();
                if (updating)
                    return;
                pending.Add(bar);
                writeDelay.Stop();
                writeDelay.Start(); // write shortly after the user stops moving
            };
            sliders.Add((d, bar, text));
            int r = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = d.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, r);
            table.Controls.Add(bar, 1, r);
            table.Controls.Add(text, 2, r);
        }

        private async Task WritePending()
        {
            writeDelay.Stop();
            foreach (var bar in pending.ToList())
            {
                pending.Remove(bar);
                var s = sliders.First(x => x.bar == bar);
                ushort? stored = await Host.WriteParamAsync(s.desc, (ushort)bar.Value);
                if (stored != null)
                    Host.ShowStatus($"{s.desc.Name}: {stored:N0} {s.desc.Unit}".TrimEnd() + " – sofort wirksam.");
            }
        }

        public override IEnumerable<Cg2Description> Covered => channels.SelectMany(c => new[] { c.Position, c.Toggle });

        public override void ParamChanged(Cg2Description param, ushort value)
        {
            foreach (var ch in channels)
            {
                if (param == ch.Present)
                    ch.Lamp.Present = value != 0;
                else if (param == ch.Address)
                    ch.Lamp.Subtitle = $"Adr. {value}";
            }
            foreach (var s in sliders.Where(x => x.desc == param))
            {
                updating = true;
                s.bar.Value = Math.Clamp(value, s.bar.Minimum, s.bar.Maximum);
                s.bar.Enabled = true;
                s.text.Text = $"{value:N0} {s.desc.Unit}".TrimEnd();
                updating = false;
            }
        }

        public override void ValuesChanged(int[] values)
        {
            foreach (var ch in channels)
            {
                int v = ValueOf(values, ch.Position);
                if (v >= 0)
                    ch.Lamp.Aspect = v == 0xFFFF ? -1 : v;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                writeDelay.Dispose();
            base.Dispose(disposing);
        }
    }

    // One light signal: dark housing with a red (top) and a green (bottom) lamp.
    // Aspect 0 = red, 1 = green, 2 = changing, -1 = unknown. Not present = grey.
    internal sealed class SignalLamp : Control
    {
        private int aspect = -1;
        private bool present = true;
        private string title = "", subtitle = "";

        public SignalLamp()
        {
            DoubleBuffered = true;
            Size = new Size(62, 96);
            Margin = new Padding(0, 0, 6, 0);
            Cursor = Cursors.Hand;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Aspect { get => aspect; set { aspect = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Present
        {
            get => present;
            set { present = value; Cursor = value ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Title { get => title; set { title = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Subtitle { get => subtitle; set { subtitle = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var housing = new Rectangle((Width - 28) / 2, 2, 28, 56);
            using (var path = Rounded(housing, 8))
            using (var body = new SolidBrush(present ? Color.FromArgb(45, 45, 45) : Color.Silver))
                g.FillPath(body, path);

            Color red = !present ? Color.Gray : aspect == 0 ? Color.Red : Color.FromArgb(80, 25, 25);
            Color green = !present ? Color.Gray : aspect == 1 ? Color.Lime : Color.FromArgb(25, 70, 25);
            using (var b = new SolidBrush(red))
                g.FillEllipse(b, housing.X + 5, housing.Y + 6, 18, 18);
            using (var b = new SolidBrush(green))
                g.FillEllipse(b, housing.X + 5, housing.Y + 31, 18, 18);
            if (present && aspect == 2)
                using (var p = new Pen(Color.Gold, 2))
                    g.DrawPath(p, Rounded(housing, 8));

            using var small = new Font(Font.FontFamily, 7.5f);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, title, small, new Rectangle(0, 62, Width, 14), ForeColor, flags);
            TextRenderer.DrawText(g, present ? subtitle : "nicht da", small, new Rectangle(0, 77, Width, 14), Color.DimGray, flags);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
