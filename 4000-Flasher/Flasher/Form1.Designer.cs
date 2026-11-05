namespace FirmwareFlasher
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.TextBox txtFirmware;
        private System.Windows.Forms.Button btnBrowse;
        private System.Windows.Forms.ComboBox cmbPorts;
        private System.Windows.Forms.Button btnRefreshPorts;
        private System.Windows.Forms.Button btnFlash;
        private System.Windows.Forms.Button btnEraseFlash;
        private System.Windows.Forms.TextBox txtLog;

        private void InitializeComponent()
        {
            txtFirmware = new TextBox();
            btnBrowse = new Button();
            cmbPorts = new ComboBox();
            btnRefreshPorts = new Button();
            btnFlash = new Button();
            btnEraseFlash = new Button();
            txtLog = new TextBox();
            SuspendLayout();
            //
            // txtFirmware
            //
            txtFirmware.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtFirmware.Location = new Point(12, 12);
            txtFirmware.Name = "txtFirmware";
            txtFirmware.Size = new Size(452, 23);
            txtFirmware.TabIndex = 0;
            //
            // btnBrowse
            //
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Location = new Point(470, 11);
            btnBrowse.Name = "btnBrowse";
            btnBrowse.Size = new Size(110, 25);
            btnBrowse.TabIndex = 1;
            btnBrowse.Text = "Durchsuchen ...";
            btnBrowse.UseVisualStyleBackColor = true;
            btnBrowse.Click += btnBrowse_Click;
            //
            // cmbPorts
            //
            cmbPorts.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPorts.Location = new Point(12, 51);
            cmbPorts.Name = "cmbPorts";
            cmbPorts.Size = new Size(110, 23);
            cmbPorts.TabIndex = 2;
            //
            // btnRefreshPorts
            //
            btnRefreshPorts.Location = new Point(128, 50);
            btnRefreshPorts.Name = "btnRefreshPorts";
            btnRefreshPorts.Size = new Size(110, 25);
            btnRefreshPorts.TabIndex = 3;
            btnRefreshPorts.Text = "Aktualisieren";
            btnRefreshPorts.UseVisualStyleBackColor = true;
            btnRefreshPorts.Click += btnRefreshPorts_Click;
            //
            // btnEraseFlash
            //
            btnEraseFlash.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnEraseFlash.Location = new Point(354, 50);
            btnEraseFlash.Name = "btnEraseFlash";
            btnEraseFlash.Size = new Size(110, 25);
            btnEraseFlash.TabIndex = 4;
            btnEraseFlash.Text = "Flash löschen";
            btnEraseFlash.UseVisualStyleBackColor = true;
            btnEraseFlash.Click += btnEraseFlash_Click;
            //
            // btnFlash
            //
            btnFlash.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnFlash.Location = new Point(470, 50);
            btnFlash.Name = "btnFlash";
            btnFlash.Size = new Size(110, 25);
            btnFlash.TabIndex = 5;
            btnFlash.Text = "Flashen";
            btnFlash.UseVisualStyleBackColor = true;
            btnFlash.Click += btnFlash_Click;
            //
            // txtLog
            //
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.Location = new Point(12, 90);
            txtLog.Multiline = true;
            txtLog.Name = "txtLog";
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Size = new Size(568, 300);
            txtLog.TabIndex = 6;
            //
            // Form1
            //
            ClientSize = new Size(592, 402);
            Controls.Add(txtFirmware);
            Controls.Add(btnBrowse);
            Controls.Add(cmbPorts);
            Controls.Add(btnRefreshPorts);
            Controls.Add(btnEraseFlash);
            Controls.Add(btnFlash);
            Controls.Add(txtLog);
            MinimumSize = new Size(500, 300);
            Name = "Form1";
            Text = "CANguru Firmware-Flasher";
            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
