using CANguru.Protocol;
using System.Drawing;
using System.Windows.Forms;

namespace CANguru.GUI.Forms.DecoderViews
{
    // Feedback decoder: 16 occupancy dots, each with its feedback address below.
    // Uses: value "Belegt" (bit mask), parameters "Kontakt k: Meldeadresse".
    public sealed class FeedbackView : DecoderView
    {
        private readonly Cg2Description occupied;
        private readonly Cg2Description?[] addresses;
        private readonly string[] addressTexts;
        private readonly BitDots dots;

        public static FeedbackView? TryCreate(IDecoderViewHost host)
        {
            var v = host.Description.Values.FirstOrDefault(d => d.Name == "Belegt" && d.Type == ProtocolConstants.CG2_TYPE_BITS);
            return v != null ? new FeedbackView(host, v) : null;
        }

        private FeedbackView(IDecoderViewHost host, Cg2Description occupied) : base(host)
        {
            this.occupied = occupied;
            dots = new BitDots(occupied.Choices);
            int n = Math.Max(1, occupied.Choices.Length);
            addresses = new Cg2Description?[n];
            addressTexts = new string[n];
            for (int k = 0; k < n; k++)
            {
                addresses[k] = Param($"Kontakt {k + 1}: Meldeadresse");
                addressTexts[k] = addresses[k] != null ? "…" : "";
            }
            dots.SubLabels = addressTexts;

            var all = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            all.Controls.Add(new Label { Text = "Belegt (rot) – darunter die Meldeadresse des Kontakts", AutoSize = true, ForeColor = Color.DimGray });
            all.Controls.Add(dots);
            Controls.Add(all);
        }

        public override IEnumerable<Cg2Description> Covered => new[] { occupied };

        public override void ParamChanged(Cg2Description param, ushort value)
        {
            int k = Array.IndexOf(addresses, param);
            if (k < 0)
                return;
            addressTexts[k] = value.ToString();
            dots.SubLabels = addressTexts;
        }

        public override void ValuesChanged(int[] values)
        {
            int v = ValueOf(values, occupied);
            if (v >= 0)
                dots.Value = v;
        }
    }
}
