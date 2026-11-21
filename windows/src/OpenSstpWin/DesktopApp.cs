using System.Runtime.InteropServices;
using OpenSstpWin.Core;

namespace OpenSstpWin;

/// <summary>Double-click entry point; command-line arguments still use the console frontend.</summary>
internal static class DesktopApp
{
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    public static void Run()
    {
        // Detach only from a console created for this process, never the user's terminal.
        var processes = new uint[2];
        if (GetConsoleProcessList(processes, (uint)processes.Length) == 1)
            FreeConsole();

        // The existing asynchronous CLI entry point is not STA. WinForms needs its own STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start Open SSTP Client: " + ex.Message,
                    "Open SSTP Client", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}

internal sealed class MainForm : Form
{
    private readonly Settings _settings;
    private readonly TextBox _host = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _remember = new() { Text = "Remember password (stored as plaintext)", AutoSize = true };
    private readonly CheckBox _verify = new() { Text = "Verify server certificate", AutoSize = true };
    private readonly CheckBox _defaultRoute = new() { Text = "Route internet traffic through VPN", AutoSize = true };
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    private readonly Button _disconnect = new() { Text = "Disconnect", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { Text = "Disconnected", AutoSize = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private VpnSession? _session;
    private bool _closing;
    private StreamWriter? _logWriter;

    public MainForm() : this(Settings.Load()) { }

    internal MainForm(Settings settings)
    {
        _settings = settings;
        Text = "Open SSTP Client";
        MinimumSize = new Size(480, 440);
        ClientSize = new Size(600, 560);
        StartPosition = FormStartPosition.CenterScreen;

        _host.Text = _settings.Host;
        _port.Value = Math.Clamp(_settings.Port, 1, 65535);
        _user.Text = _settings.Username;
        _password.Text = _settings.Password;
        _remember.Checked = !string.IsNullOrEmpty(_settings.Password);
        _verify.Checked = _settings.VerifyServerCertificate;
        _defaultRoute.Checked = _settings.AddDefaultRoute;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(layout, "Server", _host, 0);
        AddField(layout, "Port", _port, 1);
        AddField(layout, "Username", _user, 2);
        AddField(layout, "Password", _password, 3);
        AddWide(layout, _remember, 4);
        AddWide(layout, _verify, 5);
        AddWide(layout, _defaultRoute, 6);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { _connect, _disconnect, _status });
        _status.Margin = new Padding(12, 9, 0, 0);
        AddWide(layout, buttons, 7);
        AddWide(layout, new Label { Text = "Connection log", AutoSize = true, Margin = new Padding(3, 10, 3, 3) }, 8);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_log, 0, 9);
        layout.SetColumnSpan(_log, 2);
        Controls.Add(layout);

        _connect.Click += ConnectClicked;
        _disconnect.Click += (_, _) => { _disconnect.Enabled = false; _session?.Stop(); };
        FormClosing += OnFormClosing;
    }

    private static void AddField(TableLayoutPanel layout, string label, Control control, int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        control.Dock = DockStyle.Fill;
        layout.Controls.Add(control, 1, row);
    }

    private static void AddWide(TableLayoutPanel layout, Control control, int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
    }

    private async void ConnectClicked(object? sender, EventArgs e)
    {
        if (_session is not null) return;
        if (string.IsNullOrWhiteSpace(_host.Text))
        {
            MessageBox.Show(this, "Enter an SSTP server hostname or IP address.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.Host = _host.Text.Trim();
        _settings.Port = (int)_port.Value;
        _settings.Username = _user.Text;
        _settings.VerifyServerCertificate = _verify.Checked;
        _settings.AddDefaultRoute = _defaultRoute.Checked;
        // Persist only on explicit opt-in, while retaining unexposed advanced settings.
        var sessionSettings = PrepareSettings(_settings, _password.Text, _remember.Checked);
        try { _settings.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings: " + ex.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SetBusy(true);
        _status.Text = "Connecting…";
        OpenLog(sessionSettings);
        var session = new VpnSession(sessionSettings, progress => Post(() =>
        {
            _status.Text = progress.State.ToString();
        }), message => Post(() => AppendLog(message)));
        _session = session;
        try
        {
            // Adapter creation and route setup may block; never perform them on the UI thread.
            var result = await Task.Run(() => session.RunAsync(CancellationToken.None));
            if (session.State == TunnelState.Failed)
                _status.Text = "Failed: " + result.Message;
            else
                _status.Text = "Disconnected";
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
            AppendLog("Error: " + ex.Message);
        }
        finally
        {
            await session.DisposeAsync();
            _session = null;
            _logWriter?.Dispose();
            _logWriter = null;
            SetBusy(false);
            if (_closing) Close();
        }
    }

    internal static Settings PrepareSettings(Settings stored, string password, bool remember)
    {
        var session = stored.Clone();
        session.Password = password;
        stored.Password = remember ? password : string.Empty;
        return session;
    }

    private void SetBusy(bool busy)
    {
        foreach (var control in new Control[] { _host, _port, _user, _password, _remember, _verify, _defaultRoute, _connect })
            control.Enabled = !busy;
        _disconnect.Enabled = busy;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_session is null) return;
        e.Cancel = true;
        _closing = true;
        _disconnect.Enabled = false;
        _status.Text = "Disconnecting…";
        _session.Stop();
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* The window is closing. */ }
    }

    private void OpenLog(Settings settings)
    {
        if (!settings.SaveLog) return;
        try
        {
            Directory.CreateDirectory(settings.LogDirectory);
            var path = Path.Combine(settings.LogDirectory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _logWriter = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch (Exception ex) { AppendLog("Could not open log file: " + ex.Message); }
    }

    private void AppendLog(string message)
    {
        _log.AppendText(message + Environment.NewLine);
        try { _logWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}"); }
        catch (IOException) { /* Logging must not interrupt the session. */ }
    }
}
