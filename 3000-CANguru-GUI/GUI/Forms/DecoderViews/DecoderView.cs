using CANguru.Controllers;
using CANguru.Models;
using CANguru.Protocol;
using System.Drawing;
using System.Windows.Forms;

namespace CANguru.GUI.Forms.DecoderViews
{
    // What a view may use from the settings window
    public interface IDecoderViewHost
    {
        DecoderDescription Description { get; }
        // value as last read from or written to the decoder; null = not read yet
        ushort? ParamValue(Cg2Description param);
        // execute an action; true = done (the status line shows the result)
        Task<bool> ExecuteAsync(Cg2Description action);
        // write a parameter immediately (the table below follows); returns the stored value
        Task<ushort?> WriteParamAsync(Cg2Description param, ushort value);
        void ShowStatus(string text);
    }

    // View of one decoder type: an area at the top of the settings window. The general
    // table stays below. A view uses only the self-description (parameters, actions,
    // measured values, events) and finds its entries by name. If an entry it needs is
    // missing (e.g. older firmware), there is no view.
    public abstract class DecoderView : UserControl
    {
        protected readonly IDecoderViewHost Host;

        private sealed class Slider
        {
            public Cg2Description Desc = null!;
            public TrackBar Bar = null!;
            public Label Text = null!;
        }

        private readonly List<Slider> sliders = new();
        private readonly HashSet<Slider> pending = new();
        private readonly System.Windows.Forms.Timer writeDelay = new() { Interval = 400 };
        private bool updating; // slider set from the decoder, not by the user

        protected DecoderView(IDecoderViewHost host)
        {
            Host = host;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0);
            writeDelay.Tick += async (s, e) => await WritePending();
        }

        // entries offered by the view (measured values, actions, parameters edited with
        // a slider); the table below leaves them out
        public virtual IEnumerable<Cg2Description> Covered => sliders.Select(s => s.Desc);

        // a parameter was read or written; the base class updates the sliders
        public virtual void ParamChanged(Cg2Description param, ushort value)
        {
            foreach (var s in sliders.Where(x => x.Desc == param))
            {
                updating = true;
                s.Bar.Value = Math.Clamp(value, s.Bar.Minimum, s.Bar.Maximum);
                s.Bar.Enabled = true;
                s.Text.Text = $"{value:N0} {s.Desc.Unit}".TrimEnd();
                updating = false;
            }
        }

        public virtual void ValuesChanged(int[] values) { }
        public virtual void EventReceived(byte ev, byte[] data) { }

        protected Cg2Description? Param(string name) => Host.Description.Params.FirstOrDefault(d => d.Name == name);
        protected Cg2Description? Action(string name) => Host.Description.Actions.FirstOrDefault(d => d.Name == name);
        protected Cg2Description? Value(string name) => Host.Description.Values.FirstOrDefault(d => d.Name == name);

        // measured value of an entry from an alive; -1 = not contained
        protected static int ValueOf(int[] values, Cg2Description? d) =>
            d != null && d.Number < values.Length ? values[d.Number] : -1;

        // Slider for a numeric parameter with immediate effect: written shortly after the
        // user stops moving it. Row in a 3-column table: caption, slider, value.
        protected void AddSlider(TableLayoutPanel table, Cg2Description? d)
        {
            if (d == null)
                return;
            int step = Math.Max(1, (int)d.Step);
            var s = new Slider
            {
                Desc = d,
                Bar = new TrackBar
                {
                    Minimum = d.Min,
                    Maximum = Math.Max(d.Min, d.Max),
                    SmallChange = step,
                    LargeChange = step * 5,
                    TickStyle = TickStyle.None,
                    AutoSize = false,
                    Height = 28,
                    Dock = DockStyle.Fill,
                    Enabled = false // until the value is read
                },
                Text = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 6, 0, 0), Text = "–" }
            };
            s.Bar.ValueChanged += (o, e) =>
            {
                // snap to the step width (counted from min), as the decoder does
                int snapped = d.Min + (s.Bar.Value - d.Min + step / 2) / step * step;
                if (snapped != s.Bar.Value && snapped <= s.Bar.Maximum)
                {
                    s.Bar.Value = snapped;
                    return;
                }
                s.Text.Text = $"{s.Bar.Value:N0} {d.Unit}".TrimEnd();
                if (updating)
                    return;
                pending.Add(s);
                writeDelay.Stop();
                writeDelay.Start();
            };
            sliders.Add(s);
            int r = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = d.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, r);
            table.Controls.Add(s.Bar, 1, r);
            table.Controls.Add(s.Text, 2, r);
        }

        private async Task WritePending()
        {
            writeDelay.Stop();
            foreach (var s in pending.ToList())
            {
                pending.Remove(s);
                ushort? stored = await Host.WriteParamAsync(s.Desc, (ushort)s.Bar.Value);
                if (stored != null)
                    Host.ShowStatus($"{s.Desc.Name}: {stored:N0} {s.Desc.Unit}".TrimEnd() + " – gespeichert, sofort wirksam.");
            }
        }

        // three columns for sliders: caption, slider, value
        protected static TableLayoutPanel SliderTable(int captionWidth = 170)
        {
            var table = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, captionWidth));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            return table;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                writeDelay.Dispose();
            base.Dispose(disposing);
        }

        // View for a decoder type; null = none (unknown type or entries missing)
        public static DecoderView? Create(DecoderType type, IDecoderViewHost host) => type switch
        {
            DecoderType.Switch or DecoderType.Signal => StepperView.TryCreate(host),
            DecoderType.LedSignal => LedSignalView.TryCreate(host),
            DecoderType.Feedback => FeedbackView.TryCreate(host),
            DecoderType.Power => PowerView.TryCreate(host),
            _ => null
        };
    }
}
