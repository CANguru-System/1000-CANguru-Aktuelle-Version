using CANguru.Protocol;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CANguru.GUI.Forms.DecoderViews
{
    // LED signal decoder: one small signal per channel (red/green, grey = not present);
    // a click switches it (the decoder then also tells WDP). Brightness and fade time as
    // sliders with immediate effect; they replace the rows in the table.
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

            var table = SliderTable();
            foreach (var name in new[] { "Helligkeit rot", "Helligkeit grün" })
                AddSlider(table, Param(name));
            AddSlider(table, Host.Description.Params.FirstOrDefault(p => p.Name.StartsWith("Überblendzeit")));

            var all = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            all.Controls.Add(new Label { Text = "Klick auf ein Signal schaltet es um.", AutoSize = true, ForeColor = Color.DimGray });
            all.Controls.Add(lamps);
            all.Controls.Add(table);
            Controls.Add(all);
        }

        public override IEnumerable<Cg2Description> Covered => base.Covered.Concat(channels.SelectMany(c => new[] { c.Position, c.Toggle }));

        public override void ParamChanged(Cg2Description param, ushort value)
        {
            base.ParamChanged(param, value); // sliders
            foreach (var ch in channels)
            {
                if (param == ch.Present)
                    ch.Lamp.Present = value != 0;
                else if (param == ch.Address)
                    ch.Lamp.Subtitle = $"Adr. {value}";
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
