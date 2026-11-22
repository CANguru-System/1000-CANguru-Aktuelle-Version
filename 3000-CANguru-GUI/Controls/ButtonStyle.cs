using System.Runtime.CompilerServices;

namespace CANguru.Controls
{
    // One button style for all windows of the GUI: white face, thin grey border, light blue
    // under the mouse. Buttons added later (e.g. built from a decoder description) get the
    // style too, because every container is watched for new controls.
    public static class ButtonStyle
    {
        private static readonly ConditionalWeakTable<Control, object> watched = new();

        // minHeight: buttons lower than this grow downwards (0 = keep the height)
        public static void Apply(Control root, int minHeight = 0, params Control[] skip)
        {
            if (skip.Contains(root) || root is ToggleStopGoButton)
                return;
            if (root is Button b)
            {
                Style(b, minHeight);
                return;
            }
            foreach (Control c in root.Controls)
                Apply(c, minHeight, skip);
            if (!watched.TryGetValue(root, out _))
            {
                watched.Add(root, new object());
                root.ControlAdded += (s, e) => { if (e.Control != null) Apply(e.Control, minHeight, skip); };
            }
        }

        private static void Style(Button b, int minHeight)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(173, 173, 173);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(229, 241, 251);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(204, 228, 247);
            b.BackColor = Color.White;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            if (b.Height < minHeight)
                b.Height = minHeight;
        }
    }
}
