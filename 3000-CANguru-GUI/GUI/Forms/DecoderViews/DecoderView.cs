using CANguru.Controllers;
using CANguru.Models;
using CANguru.Protocol;
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
    // table stays below, so nothing is lost. A view uses only the self-description
    // (parameters, actions, measured values, events) and finds its entries by name.
    // If an entry it needs is missing (e.g. older firmware), there is no view.
    public abstract class DecoderView : UserControl
    {
        protected readonly IDecoderViewHost Host;

        protected DecoderView(IDecoderViewHost host)
        {
            Host = host;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0);
        }

        // measured values and actions offered by the view; the table leaves them out
        // (parameters always stay in the table)
        public virtual IEnumerable<Cg2Description> Covered => Array.Empty<Cg2Description>();

        public virtual void ParamChanged(Cg2Description param, ushort value) { }
        public virtual void ValuesChanged(int[] values) { }
        public virtual void EventReceived(byte ev, byte[] data) { }

        protected Cg2Description? Param(string name) => Host.Description.Params.FirstOrDefault(d => d.Name == name);
        protected Cg2Description? Action(string name) => Host.Description.Actions.FirstOrDefault(d => d.Name == name);
        protected Cg2Description? Value(string name) => Host.Description.Values.FirstOrDefault(d => d.Name == name);

        // measured value of an entry from an alive; -1 = not contained
        protected static int ValueOf(int[] values, Cg2Description? d) =>
            d != null && d.Number < values.Length ? values[d.Number] : -1;

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
