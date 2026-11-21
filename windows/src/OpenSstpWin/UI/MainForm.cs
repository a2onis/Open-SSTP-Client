using System.Reflection;
using OpenSstpWin.Core;

namespace OpenSstpWin.UI;

/// <summary>
/// The portable client window. The redesign keeps everything on one screen: a
/// configuration picker, the credentials that matter, one primary Connect action,
/// and a live session card that proves whether traffic is actually flowing.
/// Advanced options stay one click away and the connection log is collapsed
/// until it is needed, so the window is quiet unless something happens.
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
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, 8, 0),
    };
    private readonly Button _manageProfiles = new()
    {
        Text = "Manage…",
        AutoSize = true,
        Margin = new Padding(0),
    };

    private readonly TextBox _host = Ui.Text();
    private readonly NumericUpDown _port = Ui.Number(1, 65535, 443);
    private readonly TextBox _user = Ui.Text();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly CheckBox _showPassword = Ui.Check("Show", false);
    private readonly CheckBox _remember = Ui.Check("Remember password (stored as plaintext)", false);
    private readonly CheckBox _verify = Ui.Check("Verify the server certificate", true);
    private readonly CheckBox _defaultRoute = Ui.Check("Route all internet traffic through the VPN", true);

    private readonly Button _connect = new()
    {
        Text = "Connect",
        Tag = "primary",
        Height = 38,
        Width = 170,
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        Margin = new Padding(0, 0, 8, 0),
    };
    private readonly Button _disconnect = new()
    {
        Text = "Disconnect",
        Height = 38,
        Width = 120,
        Enabled = false,
        Margin = new Padding(0),
    };

    private readonly LinkLabel _moreOptions = new()
    {
        Text = "More options",
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 0),
    };
    private readonly FlowLayoutPanel _advancedPanel = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Visible = false,
        Margin = new Padding(0, 2, 0, 2),
    };

    private readonly Label _stateLabel = new()
    {
        Text = "Not connected",
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleRight,
        Font = new Font("Segoe UI", 12f, FontStyle.Bold),
    };
    private readonly Label _stateHint = new()
    {
        Text = "Choose a configuration or enter a server.",
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleRight,
        Tag = "muted",
        AutoEllipsis = true,
    };

    private readonly Label _serverValue = DetailValue();
    private readonly Label _addressValue = DetailValue();
    private readonly Label _peerValue = DetailValue();
    private readonly Label _dnsValue = DetailValue();
    private readonly Label _sentValue = DetailValue();
    private readonly Label _receivedValue = DetailValue();
    private readonly Label _durationValue = DetailValue();

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f),
        BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly Panel _logHost = new()
    {
        Dock = DockStyle.Fill,
        Height = 170,
        Visible = false,
        Padding = new Padding(16, 0, 16, 12),
    };
    private readonly Button _logToggle = new()
    {
        Text = "Show connection log",
        AutoSize = true,
        Dock = DockStyle.Top,
        TextAlign = ContentAlignment.MiddleLeft,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(16, 2, 16, 4),
    };

    private readonly MenuStrip _menu = new();
    private readonly ToolStripMenuItem _systemTheme = new("System default");
    private readonly ToolStripMenuItem _lightTheme = new("Light");
    private readonly ToolStripMenuItem _darkTheme = new("Dark");

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    private VpnSession? _session;
    private StreamWriter? _logWriter;
    private TunnelState _state = TunnelState.Idle;
    private DateTime? _connectedAt;
    private bool _busy;
    private bool _closing;
    private bool _userStopped;
    private bool _suppressProfileLoad;

    public MainForm() : this(Settings.Load())
    {
    }

    internal MainForm(Settings settings)
    {
        _settings = settings;
        _theme = settings.AppTheme;
        _profiles = ProfileStore.CreateDefault();

        Text = AppTitle;
        MinimumSize = new Size(820, 640);
        ClientSize = new Size(920, 720);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();
        ApplySettingsToForm();
        Theme.Apply(this, _theme);
        SetState(TunnelState.Idle, "Choose a configuration or enter a server.");

        _connect.Click += ConnectClicked;
        _disconnect.Click += DisconnectClicked;
        _manageProfiles.Click += (_, _) => OpenConfigurations();
        _moreOptions.LinkClicked += (_, _) => ToggleAdvancedOptions();
        _logToggle.Click += (_, _) => SetLogVisible(!_logHost.Visible);
        _profileChooser.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressProfileLoad)
            {
                LoadSelectedProfile();
            }
        };
        _showPassword.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_showPassword.Checked;
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => _tick.Dispose();
        _tick.Tick += (_, _) => UpdateDetails();
        _tick.Start();
    }

    // --------------------------------------------------------------- layout

    private void BuildLayout()
    {
        BuildMenu();
        MainMenuStrip = _menu;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(_logToggle, 0, 2);

        _logHost.Controls.Add(_log);
        root.Controls.Add(_logHost, 0, 3);

        Controls.Add(root);
        Controls.Add(_menu);
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(20, 14, 20, 8),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = AppTitle,
            AutoSize = true,
            Font = new Font("Segoe UI", 17f, FontStyle.Bold),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        };
        var subtitle = new Label
        {
            Text = "Secure SSTP VPN for Windows · portable x64",
            AutoSize = true,
            Tag = "muted",
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 0, 0, 0),
        };

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(_stateLabel, 1, 0);
        header.Controls.Add(_stateHint, 1, 1);

        return header;
    }

    private Control BuildBody()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(16, 4, 16, 0),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var connection = BuildConnectionCard();
        connection.Margin = new Padding(0, 0, 8, 0);
        var session = BuildSessionCard();
        session.Margin = new Padding(8, 0, 0, 0);

        body.Controls.Add(connection, 0, 0);
        body.Controls.Add(session, 1, 0);
        return body;
    }

    private GroupBox BuildConnectionCard()
    {
        var card = new GroupBox
        {
            Text = "  Connection  ",
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 14, 12),
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            Padding = new Padding(0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control control)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 10, 8),
            }, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 4, 0, 4);
            table.Controls.Add(control, 1, row);
        }

        void Wide(Control control)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 2, 0, 2);
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 2);
        }

        var configurationRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        configurationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        configurationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configurationRow.Controls.Add(_profileChooser, 0, 0);
        configurationRow.Controls.Add(_manageProfiles, 1, 0);

        var serverRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        serverRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        serverRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _port.Margin = new Padding(8, 0, 0, 0);
        serverRow.Controls.Add(_host, 0, 0);
        serverRow.Controls.Add(_port, 1, 0);

        var passwordRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        passwordRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        passwordRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _showPassword.Margin = new Padding(8, 0, 0, 0);
        passwordRow.Controls.Add(_password, 0, 0);
        passwordRow.Controls.Add(_showPassword, 1, 0);

        Row("Configuration", configurationRow);
        Row("Server", serverRow);
        Row("Username", _user);
        Row("Password", passwordRow);
        Wide(_remember);
        Wide(_moreOptions);

        _advancedPanel.Controls.Add(_verify);
        _advancedPanel.Controls.Add(_defaultRoute);
        Wide(_advancedPanel);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0),
        };
        actions.Controls.Add(_connect);
        actions.Controls.Add(_disconnect);
        Wide(actions);

        Wide(new Label
        {
            Text = "Administrator rights are required. While connected, all traffic uses the tunnel.",
            AutoSize = true,
            Tag = "muted",
            Margin = new Padding(0, 6, 0, 0),
        });

        card.Controls.Add(table);
        return card;
    }

    private GroupBox BuildSessionCard()
    {
        var card = new GroupBox
        {
            Text = "  Session  ",
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 14, 12),
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 0,
            AutoSize = true,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Detail(string caption, Label value)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label
            {
                Text = caption,
                AutoSize = true,
                Tag = "muted",
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 7, 10, 7),
            }, 0, row);
            value.Margin = new Padding(0, 7, 0, 7);
            table.Controls.Add(value, 1, row);
        }

        Detail("Server", _serverValue);
        Detail("Tunnel address", _addressValue);
        Detail("Peer", _peerValue);
        Detail("DNS", _dnsValue);
        Detail("Sent", _sentValue);
        Detail("Received", _receivedValue);
        Detail("Connected for", _durationValue);

        var hint = new Label
        {
            Text = "The counters above update once the tunnel is carrying traffic.",
            AutoSize = true,
            MaximumSize = new Size(330, 0),
            Tag = "muted",
            Dock = DockStyle.Top,
            Margin = new Padding(0, 18, 0, 0),
        };

        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stack.Controls.Add(table, 0, 0);
        stack.Controls.Add(hint, 0, 1);

        card.Controls.Add(stack);
        return card;
    }

    private static Label DetailValue() => new()
    {
        Text = "—",
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleRight,
        AutoEllipsis = true,
        Tag = "value",
    };

    private void ToggleAdvancedOptions()
    {
        _advancedPanel.Visible = !_advancedPanel.Visible;
        _moreOptions.Text = _advancedPanel.Visible ? "Fewer options" : "More options";
    }

    private void SetLogVisible(bool visible)
    {
        _logHost.Visible = visible;
        _logHost.Height = visible ? 180 : 0;
        if (_logHost.Parent is TableLayoutPanel parent && parent.RowStyles.Count > 3)
        {
            parent.RowStyles[3].Height = visible ? 180 : 0;
        }

        _logToggle.Text = (visible ? "Hide" : "Show") + " connection log";
        if (!visible)
        {
            return;
        }

        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Menu("&Save Configuration…", (_, _) => SaveProfileClicked()));
        file.DropDownItems.Add(Menu("&Import Profile…", (_, _) => ImportProfileClicked()));
        file.DropDownItems.Add(Menu("&Export Profile…", (_, _) => ExportProfileClicked()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Menu("E&xit", (_, _) => Close()));

        var configurations = new ToolStripMenuItem("&Configurations");
        configurations.DropDownItems.Add(Menu("&Manage Configurations…", (_, _) => OpenConfigurations()));
        configurations.DropDownItems.Add(Menu("Save Current as Configuration…", (_, _) => SaveProfileClicked()));
        configurations.DropDownItems.Add(new ToolStripSeparator());
        configurations.DropDownItems.Add(Menu("&Import Endpoint List…", (_, _) => OpenImportEndpointList()));
        configurations.DropDownItems.Add(Menu("Find &Fastest from Sources…", (_, _) => OpenFinder()));

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(Menu("Show/Hide Connection &Log", (_, _) => SetLogVisible(!_logHost.Visible)));
        view.DropDownItems.Add(Menu("Show/Hide &Advanced Options", (_, _) => ToggleAdvancedOptions()));

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

        _menu.Items.AddRange(new ToolStripItem[] { file, configurations, view, settings, help });
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
        UpdateDetails();
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

        _suppressProfileLoad = true;
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

        _suppressProfileLoad = false;
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
            return;
        }

        var profile = _profiles.Find(name);
        if (profile is null)
        {
            RefreshProfileChooser();
            return;
        }

        _settings.CopyFrom(profile.Settings);
        ApplySettingsToForm();
        RefreshProfileChooser(profile.Name);
        TrySaveSettings();
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
    }

    private void ApplyTheme(string theme)
    {
        _theme = AppThemes.Normalize(theme);
        _settings.AppTheme = _theme;
        UpdateThemeChecks();
        Theme.Apply(this, _theme);
        ApplyStateVisuals();
        UpdateDetails();
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
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.12.0";
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

        try
        {
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
        }
        catch (Exception exception)
        {
            AppendLog("Unexpected error: " + exception.Message);
            SetState(TunnelState.Failed, "The connection stopped unexpectedly. See the log.");
            SetLogVisible(true);
        }
        finally
        {
            if (!IsDisposed)
            {
                SetBusy(false);
            }
        }
    }

    private async Task RunSessionLoopAsync(Settings sessionSettings)
    {
        var attempt = 0;
        var maxAttempts = Math.Max(0, sessionSettings.ReconnectionCount);

        while (true)
        {
            var label = attempt == 0
                ? "Connecting to " + sessionSettings.Host + "…"
                : "Reconnecting (attempt " + attempt + "/" + maxAttempts + ")…";
            SetState(TunnelState.Connecting, label);
            AppendLog("=== " + label + " " + sessionSettings.Host + ":" + sessionSettings.Port + " ===");
            OpenLog(sessionSettings);

            var session = new VpnSession(
                sessionSettings,
                progress => Post(() => SetState(progress.State, progress.Message)),
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
                _connectedAt = null;
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
                SetState(TunnelState.Disconnected, "Disconnected.");
                return;
            }

            var failed = state == TunnelState.Failed;
            if (failed)
            {
                SetState(TunnelState.Failed, message);
                SetLogVisible(true);
            }
            else
            {
                SetState(TunnelState.Disconnected, "The connection ended.");
            }

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
                    SetState(TunnelState.Disconnected, "Disconnected.");
                    return;
                }

                SetState(TunnelState.Connecting,
                    "Reconnecting in " + remaining + "s (attempt " + attempt + "/" + maxAttempts + ")…");
                await Task.Delay(1000);
            }
        }
    }

    private void DisconnectClicked(object? sender, EventArgs e)
    {
        _userStopped = true;
        _disconnect.Enabled = false;
        SetState(TunnelState.Disconnecting, "Closing the tunnel…");
        _session?.Stop();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var control in new Control[]
                 {
                     _profileChooser, _manageProfiles, _host, _port, _user, _password,
                     _showPassword, _remember, _verify, _defaultRoute, _moreOptions,
                 })
        {
            control.Enabled = !busy;
        }

        _connect.Enabled = !busy;
        _disconnect.Enabled = busy;
        _menu.Enabled = !busy;
        UpdateDetails();
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
        SetState(TunnelState.Disconnecting, "Closing the tunnel…");
        _session?.Stop();
    }

    internal static Settings PrepareSettings(Settings stored, string password, bool remember)
    {
        var session = stored.Clone();
        session.Password = password;
        stored.Password = remember ? password : string.Empty;
        return session;
    }

    // --------------------------------------------------------------- state

    private void SetState(TunnelState state, string hint)
    {
        _state = state;
        if (state == TunnelState.Connected)
        {
            _connectedAt ??= DateTime.UtcNow;
        }
        else if (state is not (TunnelState.Connecting or TunnelState.Authenticating or TunnelState.Negotiating))
        {
            _connectedAt = null;
        }

        ApplyStateVisuals(hint);
        UpdateDetails();
    }

    private void ApplyStateVisuals(string? hint = null)
    {
        var dark = Theme.IsDark(_theme);
        var (text, color, defaultHint) = _state switch
        {
            TunnelState.Connecting or TunnelState.Authenticating or TunnelState.Negotiating =>
                ("Connecting…", Color.FromArgb(214, 148, 38),
                    "Negotiating TLS, authenticating and installing routes."),
            TunnelState.Connected => ("Connected", Color.FromArgb(52, 168, 83),
                "All traffic is routed through the tunnel."),
            TunnelState.Disconnecting => ("Disconnecting…", Color.FromArgb(214, 148, 38),
                "Closing the tunnel and restoring the routes."),
            TunnelState.Disconnected => ("Disconnected", Theme.Muted(dark), "The tunnel is closed."),
            TunnelState.Failed => ("Connection failed", Color.FromArgb(224, 92, 92),
                "Open the connection log for the exact reason."),
            _ => ("Not connected", Theme.Muted(dark), "Choose a configuration or enter a server."),
        };

        _stateLabel.Text = text;
        _stateLabel.ForeColor = color;
        _stateHint.Text = string.IsNullOrWhiteSpace(hint) ? defaultHint : hint;
        _stateHint.ForeColor = Theme.Muted(dark);
        Text = _state == TunnelState.Connected ? AppTitle + " — Connected" : AppTitle;

        if (_state == TunnelState.Failed)
        {
            SetLogVisible(true);
        }
    }

    private void UpdateDetails()
    {
        var session = _session;
        var host = _host.Text.Trim();
        _serverValue.Text = host.Length == 0 ? "—" : host + ":" + (int)_port.Value;
        _addressValue.Text = ValueOrDash(session?.LocalAddress);
        _peerValue.Text = ValueOrDash(session?.PeerAddress);
        _dnsValue.Text = session is { DnsServers.Count: > 0 }
            ? string.Join(", ", session.DnsServers)
            : "—";

        if (session is null)
        {
            _sentValue.Text = "—";
            _receivedValue.Text = "—";
            _durationValue.Text = "—";
            return;
        }

        var stats = session.Stats;
        _sentValue.Text = FormatTraffic(stats.PacketsToTunnel, stats.BytesToTunnel);
        _receivedValue.Text = FormatTraffic(stats.PacketsToAdapter, stats.BytesToAdapter);
        _durationValue.Text = _connectedAt is { } started
            ? FormatDuration(DateTime.UtcNow - started)
            : "—";
    }

    private static string ValueOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string FormatTraffic(long packets, long bytes) =>
        $"{packets:N0} packets · {FormatBytes(bytes)}";

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.0") + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.0") + " MB",
        _ => (bytes / (1024d * 1024 * 1024)).ToString("0.00") + " GB",
    };

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";

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
