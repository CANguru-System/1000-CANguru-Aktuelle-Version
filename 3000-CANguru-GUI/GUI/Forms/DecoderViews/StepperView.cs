using CANguru.Protocol;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CANguru.GUI.Forms.DecoderViews
{
    // Switch and semaphore: picture of the stepper with the position drawn in, and a
    // calibration assistant in four steps (the current step is highlighted), then test run.
    // Uses: value "Stellung", the four actions of group "Kalibrieren" (in their order),
    // actions "… (Testfahrt)" and "Stopp", parameter "Laufweg", event CALIBRATED.
    public sealed class StepperView : DecoderView
    {
        private static readonly string[] Instructions =
        {
            "Schritt 1: Den Schlitten langsam zum Nullpunkt fahren lassen.",
            "Schritt 2: Sobald der Schlitten am Nullpunkt anliegt, „hier ist der Nullpunkt“ drücken.",
            "Schritt 3: Den Schlitten langsam zum Endpunkt fahren lassen.",
            "Schritt 4: Sobald der Schlitten am Endpunkt anliegt, „hier ist der Endpunkt“ drücken.",
            "Warte auf die Bestätigung des Decoders ...",
            "Kalibrierung gespeichert. Jetzt mit „Testfahrt“ prüfen."
        };

        private readonly Cg2Description position;
        private readonly List<Cg2Description> steps;
        private readonly Cg2Description? test, stop, travel;

        private readonly StepperPicture picture = new();
        private readonly Label lblPosition = new() { AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 4, 0, 0) };
        private readonly Label lblInstruction = new() { AutoSize = true, MaximumSize = new Size(270, 0), Margin = new Padding(0, 6, 0, 6) };
        private readonly Label lblTravel = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 6, 0, 0) };
        private readonly Button[] stepButtons;
        private int step; // 0..3 next step, 4 waiting for CALIBRATED, 5 done

        public static StepperView? TryCreate(IDecoderViewHost host)
        {
            var d = host.Description;
            var pos = d.Values.FirstOrDefault(v => v.Name == "Stellung");
            var cal = d.Actions.Where(a => a.Group == "Kalibrieren").ToList();
            return pos != null && cal.Count == 4 ? new StepperView(host, pos, cal) : null;
        }

        private StepperView(IDecoderViewHost host, Cg2Description position, List<Cg2Description> steps) : base(host)
        {
            this.position = position;
            this.steps = steps;
            test = host.Description.Actions.FirstOrDefault(a => a.Name.Contains("Testfahrt"));
            stop = Action("Stopp");
            travel = Param("Laufweg");

            var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 12, 0) };
            left.Controls.Add(picture);
            left.Controls.Add(lblPosition);

            var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            right.Controls.Add(new Label { Text = "Kalibrier-Assistent", AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold) });
            stepButtons = new Button[4];
            for (int i = 0; i < 4; i++)
            {
                int n = i;
                var b = new Button { Text = $"{i + 1}. {steps[i].Label}", AutoSize = true, Margin = new Padding(0, 3, 0, 0), TextAlign = ContentAlignment.MiddleLeft, MinimumSize = new Size(230, 0) };
                b.Click += async (s, e) => await RunStep(n);
                stepButtons[i] = b;
                right.Controls.Add(b);
            }
            right.Controls.Add(lblInstruction);

            var drive = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0) };
            if (test != null)
            {
                var b = new Button { Text = "Testfahrt", AutoSize = true };
                b.Click += async (s, e) => await Host.ExecuteAsync(test);
                drive.Controls.Add(b);
            }
            if (stop != null)
            {
                var b = new Button { Text = "Stopp", AutoSize = true };
                b.Click += async (s, e) => { if (await Host.ExecuteAsync(stop)) StepBackAfterStop(); };
                drive.Controls.Add(b);
            }
            right.Controls.Add(drive);
            right.Controls.Add(lblTravel);

            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
            row.Controls.Add(left);
            row.Controls.Add(right);
            Controls.Add(row);

            ShowStep();
            ShowPosition(-1);
            ShowTravel();
        }

        public override IEnumerable<Cg2Description> Covered =>
            steps.Append(position).Concat(new[] { test, stop }.Where(a => a != null)!);

        private async Task RunStep(int n)
        {
            if (!await Host.ExecuteAsync(steps[n]))
                return;
            step = n + 1; // after step 4: waiting for CALIBRATED
            ShowStep();
        }

        // Stopp while driving slowly: the "here is ..." step cannot follow any more
        private void StepBackAfterStop()
        {
            if (step == 1 || step == 3)
            {
                step--;
                ShowStep();
            }
        }

        private void ShowStep()
        {
            for (int i = 0; i < 4; i++)
            {
                bool current = i == step;
                stepButtons[i].BackColor = current ? Color.Gold : SystemColors.Control;
                stepButtons[i].UseVisualStyleBackColor = !current;
                stepButtons[i].Font = new Font(Control.DefaultFont, current ? FontStyle.Bold : FontStyle.Regular);
            }
            lblInstruction.Text = Instructions[Math.Min(step, Instructions.Length - 1)];
            picture.Highlight = step <= 1 ? 1 : step <= 3 ? 2 : 0; // 1 zero point, 2 end point
        }

        private void ShowPosition(int v)
        {
            picture.Position = v;
            lblPosition.Text = "Stellung: " + (v < 0 ? "–" : position.FormatValue(v));
        }

        private void ShowTravel()
        {
            ushort? v = travel != null ? Host.ParamValue(travel) : null;
            lblTravel.Text = v == null ? "" : $"Laufweg: {v:N0} {travel!.Unit}".TrimEnd();
        }

        public override void ValuesChanged(int[] values)
        {
            int v = ValueOf(values, position);
            if (v >= 0)
                ShowPosition(v == 0xFFFF ? -1 : v);
        }

        public override void ParamChanged(Cg2Description param, ushort value)
        {
            if (param == travel)
                ShowTravel();
        }

        public override void EventReceived(byte ev, byte[] data)
        {
            if (ev != ProtocolConstants.CG2_EV_CALIBRATED)
                return;
            step = 5;
            ShowStep();
            // [channel][travel u16]
            if (data.Length >= 3)
                lblTravel.Text = $"Laufweg: {data[1] | (data[2] << 8):N0} {travel?.Unit}".TrimEnd();
        }
    }

    // The stepper picture with the position as a dot: green = reached, yellow = moving/unsure.
    // Highlight marks the target of the current calibration step.
    internal sealed class StepperPicture : Control
    {
        // positions in the picture (1280 x 720): arrows "NULLP" and "ENDP"
        private const float ImgW = 1280f, ImgH = 720f, ZeroX = 240f, EndX = 527f, TipY = 280f;
        private static Image? image;

        private int position = -1;
        private int highlight;

        public StepperPicture()
        {
            image ??= Properties.Resources.Stepper;
            DoubleBuffered = true;
            Size = new Size(320, 180);
            Margin = new Padding(0);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Position { get => position; set { position = value; Invalidate(); } }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Highlight { get => highlight; set { highlight = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float sx = Width / ImgW, sy = Height / ImgH;
            if (image != null)
                g.DrawImage(image, 0, 0, Width, Height);

            if (highlight != 0)
            {
                float cx = (highlight == 1 ? ZeroX : EndX) * sx;
                using var pen = new Pen(Color.Gold, 3);
                g.DrawRectangle(pen, cx - 52 * sx, 290 * sy, 104 * sx, (ImgH - 295) * sy);
            }

            // position 0 = zero point, 1 = end point, otherwise in between (moving / unsure)
            float x = position == 0 ? ZeroX : position == 1 ? EndX : (ZeroX + EndX) / 2;
            bool reached = position == 0 || position == 1;
            var c = new RectangleF(x * sx - 9, TipY * sy - 9, 18, 18);
            using var fill = new SolidBrush(position < 0 ? Color.Gainsboro : reached ? Color.LimeGreen : Color.Gold);
            using var ring = new Pen(Color.White, 2);
            g.FillEllipse(fill, c);
            g.DrawEllipse(ring, c);
        }
    }
}
