using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CANguru.GUI.Forms
{
    // Measured value of type bit mask as a row of dots with the bit names below,
    // e.g. "Belegt" of the feedback decoder: red = set (occupied), white = clear,
    // grey = no value yet. Eight dots per row.
    public sealed class BitDots : Control
    {
        private const int CellW = 36, CellH = 34, Dot = 16, PerRow = 8;

        private readonly string[] names;
        private int value = -1; // -1 or 0xFFFF = unknown

        public BitDots(string[] names)
        {
            this.names = names.Length > 0 ? names : Enumerable.Range(1, 16).Select(i => i.ToString()).ToArray();
            DoubleBuffered = true;
            Size = GetPreferredSize(Size.Empty);
            Margin = new Padding(0, 4, 0, 4);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => value;
            set
            {
                if (this.value == value)
                    return;
                this.value = value;
                Invalidate();
            }
        }

        private int Rows => (names.Length + PerRow - 1) / PerRow;

        public override Size GetPreferredSize(Size proposedSize) =>
            new(Math.Min(names.Length, PerRow) * CellW, Rows * CellH);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool known = value >= 0 && value != 0xFFFF;
            using var small = new Font(Font.FontFamily, 7.5f);
            using var outline = new Pen(Color.Gray);
            for (int i = 0; i < names.Length; i++)
            {
                int x = (i % PerRow) * CellW, y = (i / PerRow) * CellH;
                var circle = new Rectangle(x + (CellW - Dot) / 2, y + 2, Dot, Dot);
                bool on = known && (value & (1 << i)) != 0;
                using var fill = new SolidBrush(!known ? Color.Gainsboro : on ? Color.Red : Color.White);
                g.FillEllipse(fill, circle);
                g.DrawEllipse(outline, circle);
                var text = new Rectangle(x, y + Dot + 3, CellW, CellH - Dot - 3);
                TextRenderer.DrawText(g, names[i], small, text, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            }
        }
    }
}
