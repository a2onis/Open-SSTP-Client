using System.Reflection;
using OpenSstpWin.Core;

namespace OpenSstpWin.UI;

/// <summary>
/// The portable client window. It keeps the Android app's home screen, menu
/// (Configurations, Save/Import/Export Profile, Reload Defaults, Theme) and
/// advanced settings, and adds an automatic reconnection loop.
/// </summary>
internal sealed class MainForm : Form
{
    private const string CurrentSettingsItem = "(current settings)";
    private const string AppTitle = "Open SSTP Client";

    private Settings _settings;
    private ProfileStore _profiles;
    private string _theme;

    private readonly ComboBox _profileChooser = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 260,
        Margin = new Padding(3, 4, 3, 3),
    };
    private readonly Button _loadProfile = new() { Text = "Load", AutoSize = true, Margin = new Padding(4, 3, 4, 3) };
    private readonly Button _manageProfiles = new()
    {
        Text = "Configurations…",
        AutoSize = true,
        Margin = new Padding(4, 3, 4, 3),
    };

    private readonly TextBox _host = Ui.Text();
    private readonly NumericUpDown _port = Ui.Number(1, 65535, 443);
    private readonly TextBox _user = Ui.Text();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly CheckBox _showPassword = Ui.Check("Show password", false);
    private readonly CheckBox _remember = Ui.Check("Remember password (stored as plaintext)", false);
    private readonly CheckBox _verify = Ui.Check("Verify server certificate", true);
    private readonly CheckBox _defaultRoute = Ui.Check("Route internet traffic through the VPN", true);
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true, Margin = new Padding(4) };
    private readonly Button _disconnect = new()
    {
        Text = "Disconnect",
        AutoSize = true,
        Enabled = false,
        Margin = new Padding(4),
    };
    private readonly Label _status = new() { Text = "Disconnected", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
    };

    private readonly MenuStrip _menu = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "Disconnected" };
    private readonly ToolStripMenuItem _systemTheme = new("System default");
    private readonly ToolStripMenuItem _lightTheme = new("Light");
    private readonly ToolStripMenuItem _darkTheme = new("Dark");

    private VpnSession? _session;
    private StreamWriter? _logWriter;
    private bool _busy;
    private bool _closing;
    private bool _userStopped;

    public MainForm() : this(Settings.Load())
    {
    }

    internal MainForm(Settings settings)
    {
        _settings = settings;
        _theme = settings.AppTheme;
        _profiles = ProfileStore.CreateDefault();

        Text = AppTitle;
        MinimumSize = new Size(640, 580);
        ClientSize = new Size(760, 680);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        ApplySettingsToForm();
        Theme.Apply(this, _theme);

        _connect.Click += ConnectClicked;
        _disconnect.Click += DisconnectClicked;
        _loadProfile.Click += (_, _) => LoadSelectedProfile();
        _manageProfiles.Click += (_, _) => OpenConfigurations();
        _showPassword.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_showPassword.Checked;
        FormClosing += OnFormClosing;
    }

    // --------------------------------------------------------------- layout

    private void BuildLayout()
    {
        BuildMenu();
        MainMenuStrip = _menu;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var profiles = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        profiles.Controls.AddRange(new Control[] { _profileChooser, _loadProfile, _manageProfiles });

        AddField(layout, "Configuration", profiles, 0);
        AddField(layout, "Server", _host, 1);
        AddField(layout, "Port", _port, 2);
        AddField(layout, "Username", _user, 3);
        AddField(layout, "Password", _password, 4);
        AddWide(layout, _showPassword, 5);
        AddWide(layout, _remember, 6);
        AddWide(layout, _verify, 7);
        AddWide(layout, _defaultRoute, 8);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.AddRange(new Control[] { _connect, _disconnect, _status });
        _status.Margin = new Padding(12, 9, 0, 0);
        AddWide(layout, actions, 9);

        AddWide(layout, new Label
        {
            Text = "Connection log",
            AutoSize = true,
            Margin = new Padding(3, 10, 3, 3),
        }, 10);

        layout.RowCount = 12;
        while (layout.RowStyles.Count <= 11)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        layout.RowStyles[11] = new RowStyle(SizeType.Percent, 100);
        layout.Controls.Add(_log, 0, 11);
        layout.SetColumnSpan(_log, 2);

        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Dock = DockStyle.Bottom;

        Controls.Add(layout);
        Controls.Add(_statusStrip);
        Controls.Add(_menu);
    }

    private void AddField(TableLayoutPanel layout, string label, Control control, int row) =>
        AddRow(layout, new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 8, 3),
        }, control, row, wide: false);

    private void AddWide(TableLayoutPanel layout, Control control, int row) =>
        AddRow(layout, control, null, row, wide: true);

    private static void AddRow(TableLayoutPanel layout, Control first, Control? second, int row, bool wide)
    {
        layout.RowCount = Math.Max(layout.RowCount, row + 1);
        while (layout.RowStyles.Count <= row)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        layout.RowStyles[row] = new RowStyle(SizeType.AutoSize);
        layout.Controls.Add(first, 0, row);
        if (wide)
        {
            first.Dock = DockStyle.Fill;
            layout.SetColumnSpan(first, 2);
            return;
        }

        second!.Dock = DockStyle.Fill;
        layout.Controls.Add(second, 1, row);
    }

    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Menu("&Save Profile…", (_, _) => SaveProfileClicked()));
        file.DropDownItems.Add(Menu("&Import Profile…", (_, _) => ImportProfileClicked()));
        file.DropDownItems.Add(Menu("&Export Profile…", (_, _) => ExportProfileClicked()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Menu("E&xit", (_, _) => Close()));

        var configurations = new ToolStripMenuItem("&Configurations");
        configurations.DropDownItems.Add(Menu("&Manage Configurations…", (_, _) => OpenConfigurations()));
        configurations.DropDownItems.Add(Menu("Save Current as Profile…", (_, _) => SaveProfileClicked()));
        configurations.DropDownItems.Add(new ToolStripSeparator());
        configurations.DropDownItems.Add(Menu("&Import Endpoint List…", (_, _) => OpenImportEndpointList()));
        configurations.DropDownItems.Add(Menu("Find &Fastest from Sources…", (_, _) => OpenFinder()));

        var settings = new ToolStripMenuItem("&Settings");
        settings.DropDownItems.Add(Menu("&Advanced Settings…", (_, _) => OpenAdvancedSettings()));
        settings.DropDownItems.Add(Menu("&Reload Defaults", (_, _) => ReloadDefaults()));
        settings.DropDownItems.Add(new ToolStripSeparator());

        var themes = new ToolStripMenuItem("&Theme");
        _systemTheme.Click += (_, _) => ApplyTheme(AppThemes.System);
        _lightTheme.Click += (_, _) => ApplyTheme(AppThemes.Light);
        _darkTheme.Click += (_, _) => ApplyTheme(AppThemes.Dark);
        themes.DropDownItems.AddRange(new ToolStripItem[] { _systemTheme, _lightTheme, _darkTheme });
        settings.DropDownItems.Add(themes);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Menu("&About", (_, _) => ShowAbout()));

        _menu.Items.AddRange(new ToolStripItem[] { file, configurations, settings, help });
    }

    private static ToolStripMenuItem Menu(string text, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += handler;
        return item;
    }

    // ------------------------------------------------------------- settings

    private void ApplySettingsToForm(bool syncRemember = true)
    {
        _host.Text = _settings.Host;
        _port.Value = Math.Clamp(_settings.Port, 1, 65535);
        _user.Text = _settings.Username;
        _password.Text = _settings.Password;
        if (syncRemember)
        {
            _remember.Checked = !string.IsNullOrEmpty(_settings.Password);
        }

        _verify.Checked = _settings.VerifyServerCertificate;
        _defaultRoute.Checked = _settings.AddDefaultRoute;
        RefreshProfileChooser();
        UpdateThemeChecks();
    }

    private void ReadFormIntoSettings()
    {
        _settings.Host = _host.Text.Trim();
        _settings.Port = (int)_port.Value;
        _settings.Username = _user.Text;
        _settings.VerifyServerCertificate = _verify.Checked;
        _settings.AddDefaultRoute = _defaultRoute.Checked;
    }

    private void RefreshProfileChooser(string? selectedName = null)
    {
        _profiles = ProfileStore.CreateDefault();
        var previous = selectedName ?? (_profileChooser.SelectedItem as string);

        _profileChooser.BeginUpdate();
        _profileChooser.Items.Clear();
        _profileChooser.Items.Add(CurrentSettingsItem);
        foreach (var profile in _profiles.All)
        {
            _profileChooser.Items.Add(profile.Name);
        }

        _profileChooser.EndUpdate();

        var index = 0;
        if (!string.IsNullOrEmpty(previous) && previous != CurrentSettingsItem)
        {
            for (var i = 1; i < _profileChooser.Items.Count; i++)
            {
                if (string.Equals(_profileChooser.Items[i] as string, previous,
                        StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
        }

        if (_profileChooser.Items.Count > 0)
        {
            _profileChooser.SelectedIndex = index;
        }
    }

    private void LoadSelectedProfile()
    {
        if (_busy)
        {
            return;
        }

        var name = _profileChooser.SelectedItem as string;
        if (string.IsNullOrEmpty(name) || name == CurrentSettingsItem)
        {
            SetStatus("Choose a saved configuration in the list first.");
            return;
        }

        var profile = _profiles.Find(name);
        if (profile is null)
        {
            MessageBox.Show(this, "That configuration no longer exists.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshProfileChooser();
            return;
        }

        _settings.CopyFrom(profile.Settings);
        ApplySettingsToForm();
        RefreshProfileChooser(profile.Name);
        TrySaveSettings();
        SetStatus("Loaded configuration \"" + profile.Name + "\"");
    }

    private void OpenConfigurations(bool importOnOpen = false)
    {
        if (_busy)
        {
            return;
        }

        ReadFormIntoSettings();
        var current = _settings.Clone();
        current.Password = _password.Text;
        using var dialog = new ConfigurationsForm(_profiles, current, _theme, importOnOpen);
        var result = dialog.ShowDialog(this);
        RefreshProfileChooser();

        if (result == DialogResult.OK && dialog.AppliedSettings is Settings applied)
        {
            _settings.CopyFrom(applied);
            ApplySettingsToForm();
            TrySaveSettings();
            SetStatus("Configuration applied");
        }
    }

    private void OpenImportEndpointList() => OpenConfigurations(importOnOpen: true);

    private void OpenFinder()
    {
        if (_busy)
        {
            return;
        }

        ReadFormIntoSettings();
        var current = _settings.Clone();
        current.Password = _password.Text;
        using var dialog = new FinderForm(current, _profiles, _theme);
        var result = dialog.ShowDialog(this);
        RefreshProfileChooser();

        if (result == DialogResult.OK && dialog.AppliedSettings is Settings applied)
        {
            _settings.CopyFrom(applied);
            ApplySettingsToForm(syncRemember: false);
            TrySaveSettings();
            SetStatus("Endpoint applied");
        }
    }

    private void OpenAdvancedSettings()
    {
        if (_busy)
        {
            return;
        }

        ReadFormIntoSettings();
        var current = _settings.Clone();
        current.Password = _password.Text;
        using var dialog = new SettingsForm(current, _theme);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings.CopyFrom(dialog.Result);
        ApplySettingsToForm(syncRemember: false);
        TrySaveSettings();
        SetStatus("Advanced settings updated");
    }

    private void SaveProfileClicked()
    {
        if (_busy)
        {
            return;
        }

        ReadFormIntoSettings();
        var initial = string.IsNullOrWhiteSpace(_settings.Host) ? "New configuration" : _settings.Host;
        var name = PromptForm.Show(this, "Save configuration", "Name", initial, _theme);
        if (name is null)
        {
            return;
        }

        if (name.Length == 0)
        {
            MessageBox.Show(this, "Enter a name for the configuration.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_profiles.Find(name) is not null &&
            MessageBox.Show(this, "A configuration named \"" + name + "\" already exists. Replace it?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var snapshot = _settings.Clone();
        snapshot.Password = _password.Text;
        if (!_profiles.AddOrUpdate(name, snapshot))
        {
            MessageBox.Show(this, "A configuration with that name already exists.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        RefreshProfileChooser(name);
        SetStatus("Saved configuration \"" + name + "\"");
    }

    private void ImportProfileClicked()
    {
        if (_busy)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Import profile",
            Filter = "Profile JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var imported = SettingsFile.Import(dialog.FileName);
            _settings.CopyFrom(imported);
            ApplySettingsToForm();
            TrySaveSettings();
            SetStatus("Profile imported");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not import the profile.\n\n" + exception.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportProfileClicked()
    {
        if (_busy)
        {
            return;
        }

        ReadFormIntoSettings();
        var snapshot = _settings.Clone();
        snapshot.Password = _password.Text;

        if (MessageBox.Show(this,
                "The password is exported as plaintext. If you do not want that, blank the password first.\n\nExport now?",
                "Export profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Export profile",
            Filter = "Profile JSON (*.json)|*.json|All files (*.*)|*.*",
            FileName = SafeFileName(snapshot.Host) + ".json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            SettingsFile.Export(snapshot, dialog.FileName);
            SetStatus("Profile exported to " + dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not export the profile.\n\n" + exception.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ReloadDefaults()
    {
        if (_busy)
        {
            return;
        }

        if (MessageBox.Show(this, "Reload the default settings and clear the current endpoint and credentials?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _settings.CopyFrom(new Settings());
        ApplySettingsToForm();
        TrySaveSettings();
        SetStatus("Defaults restored");
    }

    private void ApplyTheme(string theme)
    {
        _theme = AppThemes.Normalize(theme);
        _settings.AppTheme = _theme;
        UpdateThemeChecks();
        Theme.Apply(this, _theme);
        TrySaveSettings();
    }

    private void UpdateThemeChecks()
    {
        var current = AppThemes.Normalize(_theme);
        _systemTheme.Checked = current == AppThemes.System;
        _lightTheme.Checked = current == AppThemes.Light;
        _darkTheme.Checked = current == AppThemes.Dark;
    }

    private static string SafeFileName(string? host)
    {
        var name = string.IsNullOrWhiteSpace(host) ? "profile" : host.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    private void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.11.0";
        MessageBox.Show(this,
            AppTitle + " for Windows " + version + " (portable, x64)\n\n" +
            "A Windows port of Open-SSTP-Client by kittoku, using the Wintun layer-3 adapter.\n\n" +
            "Settings: " + Settings.FilePath + "\n" +
            "Profiles: " + Settings.ProfilesPath + "\n" +
            "Logs: " + _settings.LogDirectory,
            "About " + AppTitle,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ------------------------------------------------------------ connect

    private async void ConnectClicked(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        ReadFormIntoSettings();
        var sessionSettings = PrepareSettings(_settings, _password.Text, _remember.Checked);
        var errors = sessionSettings.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Cannot connect",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _settings.Save();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not save settings: " + exception.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _userStopped = false;
        _closing = false;
        SetBusy(true);
        await RunSessionLoopAsync(sessionSettings);
        if (!IsDisposed)
        {
            SetBusy(false);
        }
    }

    private async Task RunSessionLoopAsync(Settings sessionSettings)
    {
        var attempt = 0;
        var maxAttempts = Math.Max(0, sessionSettings.ReconnectionCount);

        while (true)
        {
            var label = attempt == 0
                ? "Connecting…"
                : "Reconnecting (attempt " + attempt + "/" + maxAttempts + ")…";
            SetStatus(label);
            AppendLog("=== " + label + " " + sessionSettings.Host + ":" + sessionSettings.Port + " ===");
            OpenLog(sessionSettings);

            var session = new VpnSession(
                sessionSettings,
                progress => Post(() => SetStatus(Describe(progress))),
                message => Post(() => AppendLog(message)));
            _session = session;

            var state = TunnelState.Idle;
            var message = string.Empty;
            try
            {
                var result = await Task.Run(() => session.RunAsync(CancellationToken.None));
                state = session.State;
                message = result.Message;
            }
            catch (Exception exception)
            {
                state = TunnelState.Failed;
                message = exception.Message;
                AppendLog("Error: " + exception.Message);
            }
            finally
            {
                _session = null;
                try
                {
                    await session.DisposeAsync();
                }
                catch
                {
                    // Teardown already reports its own errors.
                }

                CloseLog();
            }

            if (_closing)
            {
                Close();
                return;
            }

            if (_userStopped)
            {
                SetStatus("Disconnected");
                return;
            }

            var failed = state == TunnelState.Failed;
            SetStatus(failed ? "Failed: " + message : "Disconnected");

            if (!sessionSettings.EnableReconnection)
            {
                return;
            }

            if (attempt >= maxAttempts)
            {
                if (failed)
                {
                    AppendLog("Reconnection attempts exhausted.");
                }

                return;
            }

            attempt++;
            var seconds = Math.Max(1, sessionSettings.ReconnectionIntervalSeconds);
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                if (_closing)
                {
                    Close();
                    return;
                }

                if (_userStopped)
                {
                    SetStatus("Disconnected");
                    return;
                }

                SetStatus("Reconnecting in " + remaining + "s (attempt " + attempt + "/" + maxAttempts + ")…");
                await Task.Delay(1000);
            }
        }
    }

    private void DisconnectClicked(object? sender, EventArgs e)
    {
        _userStopped = true;
        _disconnect.Enabled = false;
        SetStatus("Disconnecting…");
        _session?.Stop();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var control in new Control[]
                 {
                     _profileChooser, _loadProfile, _manageProfiles, _host, _port, _user, _password,
                     _showPassword, _remember, _verify, _defaultRoute, _connect,
                 })
        {
            control.Enabled = !busy;
        }

        _disconnect.Enabled = busy;
        _menu.Enabled = !busy;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing || !_busy)
        {
            return;
        }

        e.Cancel = true;
        _closing = true;
        _userStopped = true;
        _disconnect.Enabled = false;
        SetStatus("Disconnecting…");
        _session?.Stop();
    }

    internal static Settings PrepareSettings(Settings stored, string password, bool remember)
    {
        var session = stored.Clone();
        session.Password = password;
        stored.Password = remember ? password : string.Empty;
        return session;
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
        _statusLabel.Text = message;
    }

    private static string Describe(TunnelProgress progress) => progress.State switch
    {
        TunnelState.Connected => "Connected: " + progress.Message,
        TunnelState.Failed => "Failed: " + progress.Message,
        TunnelState.Disconnecting => "Disconnecting…",
        TunnelState.Disconnected => "Disconnected",
        _ => progress.Message,
    };

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    private void OpenLog(Settings settings)
    {
        if (!settings.SaveLog)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(settings.LogDirectory);
            var path = Path.Combine(settings.LogDirectory,
                "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            _logWriter = new StreamWriter(path, append: true) { AutoFlush = true };
            AppendLog("Log file: " + path);
        }
        catch (Exception exception)
        {
            AppendLog("Could not open log file: " + exception.Message);
        }
    }

    private void AppendLog(string message)
    {
        _log.AppendText(message + Environment.NewLine);
        try
        {
            _logWriter?.WriteLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message);
        }
        catch (IOException)
        {
            // Logging must not interrupt the session.
        }
    }

    private void CloseLog()
    {
        try
        {
            _logWriter?.Dispose();
        }
        catch (IOException)
        {
            // Ignore a failed flush while disconnecting.
        }

        _logWriter = null;
    }

    private void TrySaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not save settings: " + exception.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
