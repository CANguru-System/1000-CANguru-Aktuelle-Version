using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CANguru.GUI.Forms
{
    // Measured value of type bit mask as a row of dots with the bit names below,
    // e.g. "Belegt" of the feedback decoder: red = set (occupied), white = clear,
    // grey = no value yet. Eight dots per row. Optional second text line per dot
    // (SubLabels, e.g. the feedback address of each contact).
    public sealed class BitDots : Control
    {
        private const int CellW = 40, Dot = 16, PerRow = 8, LineH = 13;

        private readonly string[] names;
        private string[]? subLabels;
        private int value = -1; // -1 or 0xFFFF = unknown

        public BitDots(string[] names)
        {
            this.names = names.Length > 0 ? names : Enumerable.Range(1, 16).Select(i => i.ToString()).ToArray();
            DoubleBuffered = true;
            Size = GetPreferredSize(Size.Empty);
            Margin = new Padding(0, 4, 0, 4);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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

        // Second text line below each dot (null = none)
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string[]? SubLabels
        {
            get => subLabels;
            set
            {
                subLabels = value;
                Size = GetPreferredSize(Size.Empty);
                Invalidate();
            }
        }

        private int CellH => Dot + 4 + LineH * (subLabels == null ? 1 : 2) + 3;
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
            const TextFormatFlags centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding;
            for (int i = 0; i < names.Length; i++)
            {
                int x = (i % PerRow) * CellW, y = (i / PerRow) * CellH;
                var circle = new Rectangle(x + (CellW - Dot) / 2, y + 2, Dot, Dot);
                bool on = known && (value & (1 << i)) != 0;
                using var fill = new SolidBrush(!known ? Color.Gainsboro : on ? Color.Red : Color.White);
                g.FillEllipse(fill, circle);
                g.DrawEllipse(outline, circle);
                int ty = y + Dot + 4;
                TextRenderer.DrawText(g, names[i], small, new Rectangle(x, ty, CellW, LineH), ForeColor, centered);
                if (subLabels != null && i < subLabels.Length)
                    TextRenderer.DrawText(g, subLabels[i], small, new Rectangle(x, ty + LineH, CellW, LineH), Color.DimGray, centered);
            }
        }
    }
}
