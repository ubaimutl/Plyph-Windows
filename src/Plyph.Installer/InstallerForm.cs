using System.Drawing;
using System.Windows.Forms;

namespace Plyph.Installer;

public sealed class InstallerForm : Form
{
    private readonly TextBox _dirTextBox;
    private readonly CheckBox _startMenuChk;
    private readonly CheckBox _desktopChk;
    private readonly CheckBox _startupChk;
    private readonly CheckBox _launchNowChk;
    private readonly Label _statusLabel;
    private readonly Button _installButton;
    private readonly Button _uninstallButton;

    public InstallerForm(bool startInUninstallMode = false)
    {
        Text = startInUninstallMode ? "Uninstall Plyph" : "Plyph Setup";
        Size = new Size(540, 390);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(26, 28, 33);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);

        var title = new Label
        {
            Text = "Plyph for Windows",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(22, 18),
            AutoSize = true
        };
        var subtitle = new Label
        {
            Text = "Use AI on selected text anywhere in Windows (Per-User Installation — No Admin Required)",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(175, 185, 200),
            Location = new Point(24, 48),
            AutoSize = true
        };
        Controls.Add(title);
        Controls.Add(subtitle);

        var dirLabel = new Label
        {
            Text = "Installation folder:",
            Location = new Point(24, 86),
            AutoSize = true
        };
        _dirTextBox = new TextBox
        {
            Text = InstallOptions.DefaultInstallDirectory,
            Location = new Point(24, 110),
            Width = 390,
            BackColor = Color.FromArgb(44, 48, 56),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        var browseBtn = new Button
        {
            Text = "Browse…",
            Location = new Point(422, 108),
            Size = new Size(80, 27),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(54, 59, 68),
            ForeColor = Color.White
        };
        browseBtn.FlatAppearance.BorderSize = 0;
        browseBtn.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { SelectedPath = _dirTextBox.Text };
            if (fbd.ShowDialog(this) == DialogResult.OK)
                _dirTextBox.Text = fbd.SelectedPath;
        };
        Controls.AddRange(new Control[] { dirLabel, _dirTextBox, browseBtn });

        _startMenuChk = new CheckBox
        {
            Text = "Create Start Menu shortcut",
            Checked = true,
            Location = new Point(24, 152),
            AutoSize = true
        };
        _desktopChk = new CheckBox
        {
            Text = "Create Desktop shortcut",
            Checked = true,
            Location = new Point(24, 182),
            AutoSize = true
        };
        _startupChk = new CheckBox
        {
            Text = "Launch Plyph when Windows starts",
            Checked = true,
            Location = new Point(24, 212),
            AutoSize = true
        };
        _launchNowChk = new CheckBox
        {
            Text = "Start Plyph immediately after installation",
            Checked = true,
            Location = new Point(24, 242),
            AutoSize = true
        };
        Controls.AddRange(new Control[] { _startMenuChk, _desktopChk, _startupChk, _launchNowChk });

        _statusLabel = new Label
        {
            Text = "Ready to install.",
            ForeColor = Color.FromArgb(150, 200, 255),
            Location = new Point(24, 278),
            AutoSize = true
        };
        Controls.Add(_statusLabel);

        _uninstallButton = new Button
        {
            Text = "Uninstall",
            Location = new Point(24, 306),
            Size = new Size(110, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(145, 35, 42),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        _uninstallButton.FlatAppearance.BorderSize = 0;
        _uninstallButton.Click += (_, _) => RunUninstall();
        Controls.Add(_uninstallButton);

        var cancelBtn = new Button
        {
            Text = "Close",
            Location = new Point(300, 306),
            Size = new Size(92, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(54, 59, 68),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        cancelBtn.FlatAppearance.BorderSize = 0;
        cancelBtn.Click += (_, _) => Close();
        Controls.Add(cancelBtn);

        _installButton = new Button
        {
            Text = "Install Plyph",
            Location = new Point(400, 306),
            Size = new Size(102, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(53, 132, 228),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _installButton.FlatAppearance.BorderSize = 0;
        _installButton.Click += async (_, _) => await RunInstallAsync();
        Controls.Add(_installButton);

        if (startInUninstallMode)
        {
            Shown += (_, _) => RunUninstall();
        }
    }

    private async Task RunInstallAsync()
    {
        _installButton.Enabled = false;
        _uninstallButton.Enabled = false;
        var progress = new Progress<string>(msg => _statusLabel.Text = msg);

        try
        {
            await InstallerEngine.InstallAsync(new InstallOptions
            {
                InstallDirectory = _dirTextBox.Text.Trim(),
                CreateStartMenuShortcut = _startMenuChk.Checked,
                CreateDesktopShortcut = _desktopChk.Checked,
                LaunchAtStartup = _startupChk.Checked,
                LaunchAfterInstall = _launchNowChk.Checked
            }, progress);

            MessageBox.Show(this, "Plyph has been installed successfully!", "Plyph Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Plyph Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _installButton.Enabled = true;
            _uninstallButton.Enabled = true;
        }
    }

    private void RunUninstall()
    {
        var res = MessageBox.Show(
            this,
            "Uninstall Plyph from your account?\n\nClick Yes to uninstall (keeping your saved settings), or Cancel to abort.",
            "Uninstall Plyph",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (res != DialogResult.Yes)
            return;

        try
        {
            var progress = new Progress<string>(msg => _statusLabel.Text = msg);
            InstallerEngine.Uninstall(removeUserData: false, progress);
            MessageBox.Show(this, "Plyph has been uninstalled.", "Uninstall Plyph", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Uninstall Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
