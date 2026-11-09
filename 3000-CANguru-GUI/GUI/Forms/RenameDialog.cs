using System.Drawing;
using System.Windows.Forms;

namespace CANguru.GUI.Forms
{
    // Asks for a decoder name. Empty = back to the type name. null = cancelled.
    public static class RenameDialog
    {
        public static string? Ask(IWin32Window owner, string typeText, string currentName)
        {
            using var form = new Form
            {
                Text = "Decoder umbenennen",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(360, 120)
            };
            var hint = new Label
            {
                Text = $"Eigener Name für diesen Decoder ({typeText}).\r\nLeer lassen, um wieder den Typ anzuzeigen.",
                AutoSize = true,
                Location = new Point(12, 10)
            };
            var box = new TextBox { Text = currentName, Location = new Point(12, 48), Width = 336, MaxLength = 40 };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(192, 84), Width = 75 };
            var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, Location = new Point(273, 84), Width = 75 };
            form.Controls.AddRange(new Control[] { hint, box, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            box.SelectAll();
            return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
        }
    }
}
