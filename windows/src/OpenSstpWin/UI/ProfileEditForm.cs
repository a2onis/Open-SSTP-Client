using OpenSstpWin.Core;

namespace OpenSstpWin.UI;

/// <summary>Edits the name, endpoint and credentials of one saved configuration.</summary>
internal sealed class ProfileEditForm : Form
{
    private readonly Settings _settings;
    private readonly string _theme;
    private readonly TextBox _name = Ui.Text();
    private readonly TextBox _host = Ui.Text();
    private readonly NumericUpDown _port = Ui.Number(1, 65535, 443);
    private readonly TextBox _user = Ui.Text();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly CheckBox _showPassword = Ui.Check("Show password", false);

    internal ProfileEditForm(string name, Settings settings, string theme)
    {
        _settings = settings.Clone();
        _theme = theme;

        Text = string.IsNullOrWhiteSpace(name) ? "Add configuration" : "Edit configuration";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(540, 360);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        _name.Text = name;
        _host.Text = _settings.Host;
        _port.Value = Math.Clamp(_settings.Port, 1, 65535);
        _user.Text = _settings.Username;
        _password.Text = _settings.Password;

        var table = Ui.Form();
        Ui.Row(table, "Name", _name);
        Ui.Row(table, "Server", _host);
        Ui.Row(table, "Port", _port);
        Ui.Row(table, "Username", _user);
        Ui.Row(table, "Password", _password);
        Ui.Wide(table, _showPassword);

        var advanced = new Button { Text = "Advanced settings…", AutoSize = true };
        advanced.Click += (_, _) => OpenAdvancedSettings();
        Ui.Wide(table, Ui.ButtonRow(advanced));
        Ui.Wide(table,
            Hint("Credentials are stored as plaintext in profiles.json. The server port defaults to 443."));

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Margin = new Padding(4) };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(4),
        };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 10),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(table);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        _showPassword.CheckedChanged += (_, _) =>
            _password.UseSystemPasswordChar = !_showPassword.Checked;
        FormClosing += OnClosing;
        Theme.Apply(this, theme);
        ActiveControl = _name;
    }

    internal string ProfileName => _name.Text.Trim();

    internal Settings Result => _settings;

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 4, 3, 8),
    };

    private void OpenAdvancedSettings()
    {
        Collect();
        using var dialog = new SettingsForm(_settings.Clone(), _theme);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _settings.CopyFrom(dialog.Result);
        }
    }

    private void Collect()
    {
        _settings.Host = EndpointParser.NormalizeHost(_host.Text);
        _settings.Port = (int)_port.Value;
        _settings.Username = _user.Text;
        _settings.Password = _password.Text;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK)
        {
            return;
        }

        if (ProfileName.Length == 0)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Enter a name for this configuration.", "Add configuration",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ActiveControl = _name;
            return;
        }

        Collect();
        var errors = _settings.Validate(requireEndpoint: true);
        if (errors.Count > 0)
        {
            e.Cancel = true;
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Invalid configuration",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
