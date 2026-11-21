namespace OpenSstpWin.UI;

/// <summary>
/// The Windows counterpart of the Android SETTING tab: TLS, proxy, PPP, DNS,
/// routing, reconnection and logging options.
/// </summary>
internal sealed class SettingsForm : Form
{
    private sealed record Choice(string Text, string Value)
    {
        public override string ToString() => Text;
    }

    private readonly Settings _result;

    private readonly ComboBox _tls = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _verify = Ui.Check("Verify the server certificate", true);
    private readonly CheckBox _customCa = Ui.Check("Trust only the certificates in this bundle", false);
    private readonly TextBox _caPath = Ui.Text();
    private readonly Button _caBrowse = new() { Text = "Browse…", AutoSize = true };
    private readonly CheckBox _customSni = Ui.Check("Send a custom TLS server name (SNI)", false);
    private readonly TextBox _sni = Ui.Text();
    private readonly CheckBox _useSuites = Ui.Check("Restrict cipher suites (best effort)", false);
    private readonly TextBox _suites = Ui.Text();

    private readonly CheckBox _useProxy = Ui.Check("Connect through an HTTP CONNECT proxy", false);
    private readonly TextBox _proxyHost = Ui.Text();
    private readonly NumericUpDown _proxyPort = Ui.Number(1, 65535, 8080);
    private readonly TextBox _proxyUser = Ui.Text();
    private readonly TextBox _proxyPassword = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };

    private readonly NumericUpDown _mru = Ui.Number(576, 2000, 1500);
    private readonly NumericUpDown _mtu = Ui.Number(576, 2000, 1500);
    private readonly NumericUpDown _authTimeout = Ui.Number(3, 300, 15);
    private readonly CheckBox _authPap = Ui.Check(Auth.Pap, true);
    private readonly CheckBox _authMsChap = Ui.Check(Auth.MsChapV2, true);
    private readonly CheckBox _authEap = Ui.Check(Auth.EapMsChapV2, false);
    private readonly CheckBox _ipv4 = Ui.Check("Enable IPv4 (IPCP)", true);
    private readonly CheckBox _staticIp = Ui.Check("Request a static IPv4 address", false);
    private readonly TextBox _staticIpText = Ui.Text();
    private readonly CheckBox _ipv6 = Ui.Check("Enable IPv6 (IPv6CP)", false);

    private readonly CheckBox _requestDns = Ui.Check("Request DNS servers from the VPN server", true);
    private readonly CheckBox _customDns = Ui.Check("Use a custom DNS server", false);
    private readonly TextBox _customDnsText = Ui.Text();

    private readonly CheckBox _defaultRoute = Ui.Check("Route internet traffic through the VPN", true);
    private readonly CheckBox _privateRoutes = Ui.Check("Route private networks (RFC1918) through the VPN", true);
    private readonly CheckBox _addCustomRoutes = Ui.Check("Add the custom routes below", true);
    private readonly TextBox _customRoutes = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        MinimumSize = new Size(0, 70),
    };

    private readonly CheckBox _reconnect = Ui.Check("Reconnect automatically after a drop", false);
    private readonly NumericUpDown _reconnectCount = Ui.Number(0, 50, 3);
    private readonly NumericUpDown _reconnectInterval = Ui.Number(1, 3600, 10);

    private readonly CheckBox _saveLog = Ui.Check("Save a session log", true);
    private readonly TextBox _logFolder = Ui.Text();
    private readonly Button _logBrowse = new() { Text = "Browse…", AutoSize = true };
    private readonly TextBox _adapter = Ui.Text();

    internal SettingsForm(Settings settings, string theme)
    {
        _result = settings.Clone();

        Text = "Advanced settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 620);
        MinimumSize = new Size(580, 480);
        ShowInTaskbar = false;

        Populate();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildTlsTab());
        tabs.TabPages.Add(BuildProxyTab());
        tabs.TabPages.Add(BuildPppTab());
        tabs.TabPages.Add(BuildDnsTab());
        tabs.TabPages.Add(BuildRoutingTab());
        tabs.TabPages.Add(BuildReconnectionTab());
        tabs.TabPages.Add(BuildLoggingTab());

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Margin = new Padding(4) };
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
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 10),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        AcceptButton = ok;
        CancelButton = cancel;

        _caBrowse.Click += (_, _) => BrowseCaBundle();
        _logBrowse.Click += (_, _) => BrowseLogFolder();
        foreach (var box in new[]
                 {
                     _customCa, _customSni, _useSuites, _useProxy, _ipv4, _staticIp, _customDns,
                     _addCustomRoutes, _reconnect, _saveLog,
                 })
        {
            box.CheckedChanged += (_, _) => SyncEnabled();
        }

        SyncEnabled();
        FormClosing += OnClosing;
        Theme.Apply(this, theme);
    }

    internal Settings Result => _result;

    private void Populate()
    {
        _tls.Items.Add(new Choice("Default (let Windows negotiate)", TlsModes.Default));
        _tls.Items.Add(new Choice("TLS 1.2", TlsModes.Tls12));
        _tls.Items.Add(new Choice("TLS 1.3", TlsModes.Tls13));
        _tls.SelectedIndex = Math.Max(0, TlsModes.All.ToList().IndexOf(_result.TlsVersion));

        _verify.Checked = _result.VerifyServerCertificate;
        _customCa.Checked = _result.UseCustomCa;
        _caPath.Text = _result.CaBundlePath;
        _customSni.Checked = _result.UseCustomSni;
        _sni.Text = _result.CustomSniHostname;
        _useSuites.Checked = _result.UseCipherSuites;
        _suites.Text = string.Join(", ", _result.CipherSuites);

        _useProxy.Checked = _result.UseProxy;
        _proxyHost.Text = _result.ProxyHost;
        SetNumber(_proxyPort, _result.ProxyPort);
        _proxyUser.Text = _result.ProxyUsername;
        _proxyPassword.Text = _result.ProxyPassword;

        SetNumber(_mru, _result.Mru);
        SetNumber(_mtu, _result.Mtu);
        SetNumber(_authTimeout, _result.AuthTimeoutSeconds);
        _authPap.Checked = _result.AuthProtocols.Contains(Auth.Pap, StringComparer.OrdinalIgnoreCase);
        _authMsChap.Checked = _result.AuthProtocols.Contains(Auth.MsChapV2, StringComparer.OrdinalIgnoreCase);
        _authEap.Checked = _result.AuthProtocols.Contains(Auth.EapMsChapV2, StringComparer.OrdinalIgnoreCase);
        _ipv4.Checked = _result.EnableIpv4;
        _staticIp.Checked = _result.RequestStaticIpv4;
        _staticIpText.Text = _result.StaticIpv4Address;
        _ipv6.Checked = _result.EnableIpv6;

        _requestDns.Checked = _result.RequestDns;
        _customDns.Checked = _result.UseCustomDns;
        _customDnsText.Text = _result.CustomDnsAddress;

        _defaultRoute.Checked = _result.AddDefaultRoute;
        _privateRoutes.Checked = _result.RoutePrivateNetworks;
        _addCustomRoutes.Checked = _result.AddCustomRoutes;
        _customRoutes.Text = string.Join(Environment.NewLine, _result.CustomRoutes);

        _reconnect.Checked = _result.EnableReconnection;
        SetNumber(_reconnectCount, _result.ReconnectionCount);
        SetNumber(_reconnectInterval, _result.ReconnectionIntervalSeconds);

        _saveLog.Checked = _result.SaveLog;
        _logFolder.Text = _result.LogFolder;
        _adapter.Text = _result.AdapterName;
    }

    private static void SetNumber(NumericUpDown number, int value) =>
        number.Value = Math.Clamp(value, (int)number.Minimum, (int)number.Maximum);

    private TabPage BuildTlsTab()
    {
        var table = Ui.Form();
        Ui.Row(table, "TLS version", _tls);
        Ui.Wide(table, _verify);
        Ui.Wide(table, _customCa);
        Ui.Wide(table, PathPanel(_caPath, _caBrowse));
        Ui.Wide(table, Hint("A PEM file or a folder of certificates. When enabled, only these roots are trusted."));
        Ui.Wide(table, _customSni);
        Ui.Row(table, "SNI hostname", _sni);
        Ui.Wide(table, _useSuites);
        Ui.Row(table, "Cipher suites", _suites);
        Ui.Wide(table, Hint("Comma-separated IANA names, for debugging. Windows Schannel may ignore this list."));
        return Ui.Tab("SSL / TLS", table);
    }

    private TabPage BuildProxyTab()
    {
        var table = Ui.Form();
        Ui.Wide(table, _useProxy);
        Ui.Row(table, "Proxy host", _proxyHost);
        Ui.Row(table, "Proxy port", _proxyPort);
        Ui.Row(table, "Proxy username", _proxyUser);
        Ui.Row(table, "Proxy password", _proxyPassword);
        Ui.Wide(table, Hint("The proxy is used only to reach the SSTP server; passwords are stored in plaintext."));
        return Ui.Tab("Proxy", table);
    }

    private TabPage BuildPppTab()
    {
        var table = Ui.Form();
        Ui.Row(table, "MRU", _mru);
        Ui.Row(table, "MTU", _mtu);
        Ui.Row(table, "Authentication timeout (s)", _authTimeout);
        var protocols = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        protocols.Controls.AddRange(new Control[] { _authPap, _authMsChap, _authEap });
        Ui.Wide(table, Hint("PPP authentication protocols (at least one):"));
        Ui.Wide(table, protocols);
        Ui.Wide(table, _ipv4);
        Ui.Wide(table, _staticIp);
        Ui.Row(table, "Static IPv4 address", _staticIpText);
        Ui.Wide(table, _ipv6);
        return Ui.Tab("PPP", table);
    }

    private TabPage BuildDnsTab()
    {
        var table = Ui.Form();
        Ui.Wide(table, _requestDns);
        Ui.Wide(table, _customDns);
        Ui.Row(table, "Custom DNS server(s)", _customDnsText);
        Ui.Wide(table, Hint("One or more IPv4 addresses separated by commas. A host route keeps them inside the tunnel."));
        return Ui.Tab("DNS", table);
    }

    private TabPage BuildRoutingTab()
    {
        var table = Ui.Form();
        Ui.Wide(table, _defaultRoute);
        Ui.Wide(table, _privateRoutes);
        Ui.Wide(table, _addCustomRoutes);
        Ui.Row(table, "Custom routes", _customRoutes);
        Ui.Wide(table, Hint("One prefix per line, for example 10.20.0.0/16."));
        Ui.Wide(table, Hint(
            "App-based rules (Select Allowed/Disallowed Apps) exist only on Android. " +
            "The Wintun adapter is machine-wide, so every application uses the tunnel."));
        return Ui.Tab("Routing", table);
    }

    private TabPage BuildReconnectionTab()
    {
        var table = Ui.Form();
        Ui.Wide(table, _reconnect);
        Ui.Row(table, "Attempts", _reconnectCount);
        Ui.Row(table, "Interval (s)", _reconnectInterval);
        Ui.Wide(table, Hint("Retries start after a failure or an unexpected disconnect, not after Disconnect."));
        return Ui.Tab("Reconnection", table);
    }

    private TabPage BuildLoggingTab()
    {
        var table = Ui.Form();
        Ui.Wide(table, _saveLog);
        Ui.Wide(table, PathPanel(_logFolder, _logBrowse));
        Ui.Wide(table, Hint("Leave empty for %LOCALAPPDATA%\\OpenSstpWin\\logs."));
        Ui.Row(table, "Adapter name", _adapter);
        return Ui.Tab("Logging", table);
    }

    private static TableLayoutPanel PathPanel(TextBox text, Button browse)
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(text, 0, 0);
        panel.Controls.Add(browse, 1, 0);
        return panel;
    }

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 2, 3, 8),
    };

    private void SyncEnabled()
    {
        _caPath.Enabled = _customCa.Checked;
        _caBrowse.Enabled = _customCa.Checked;
        _sni.Enabled = _customSni.Checked;
        _suites.Enabled = _useSuites.Checked;
        foreach (var control in new Control[] { _proxyHost, _proxyPort, _proxyUser, _proxyPassword })
        {
            control.Enabled = _useProxy.Checked;
        }

        _staticIp.Enabled = _ipv4.Checked;
        _staticIpText.Enabled = _ipv4.Checked && _staticIp.Checked;
        _customDnsText.Enabled = _customDns.Checked;
        _customRoutes.Enabled = _addCustomRoutes.Checked;
        _reconnectCount.Enabled = _reconnect.Checked;
        _reconnectInterval.Enabled = _reconnect.Checked;
        _logFolder.Enabled = _saveLog.Checked;
        _logBrowse.Enabled = _saveLog.Checked;
    }

    private void BrowseCaBundle()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a CA bundle",
            Filter = "Certificates (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _caPath.Text = dialog.FileName;
        }
    }

    private void BrowseLogFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Folder for session logs",
            SelectedPath = string.IsNullOrWhiteSpace(_logFolder.Text) ? _result.LogDirectory : _logFolder.Text,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _logFolder.Text = dialog.SelectedPath;
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK)
        {
            return;
        }

        Collect();
        var errors = _result.Validate(requireEndpoint: false);
        if (errors.Count == 0)
        {
            return;
        }

        e.Cancel = true;
        MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Invalid settings",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void Collect()
    {
        _result.TlsVersion = (_tls.SelectedItem as Choice)?.Value ?? TlsModes.Default;
        _result.VerifyServerCertificate = _verify.Checked;
        _result.UseCustomCa = _customCa.Checked;
        _result.CaBundlePath = _customCa.Checked ? _caPath.Text.Trim() : string.Empty;
        _result.UseCustomSni = _customSni.Checked;
        _result.CustomSniHostname = _sni.Text.Trim();
        _result.UseCipherSuites = _useSuites.Checked;
        _result.CipherSuites = Ui.SplitList(_suites.Text).ToList();

        _result.UseProxy = _useProxy.Checked;
        _result.ProxyHost = _proxyHost.Text.Trim();
        _result.ProxyPort = (int)_proxyPort.Value;
        _result.ProxyUsername = _proxyUser.Text;
        _result.ProxyPassword = _proxyPassword.Text;

        _result.Mru = (int)_mru.Value;
        _result.Mtu = (int)_mtu.Value;
        _result.AuthTimeoutSeconds = (int)_authTimeout.Value;

        var protocols = new List<string>();
        if (_authPap.Checked) protocols.Add(Auth.Pap);
        if (_authMsChap.Checked) protocols.Add(Auth.MsChapV2);
        if (_authEap.Checked) protocols.Add(Auth.EapMsChapV2);
        _result.AuthProtocols = protocols;

        _result.EnableIpv4 = _ipv4.Checked;
        _result.RequestStaticIpv4 = _ipv4.Checked && _staticIp.Checked;
        _result.StaticIpv4Address = _staticIpText.Text.Trim();
        _result.EnableIpv6 = _ipv6.Checked;

        _result.RequestDns = _requestDns.Checked;
        _result.UseCustomDns = _customDns.Checked;
        _result.CustomDnsAddress = _customDnsText.Text.Trim();

        _result.AddDefaultRoute = _defaultRoute.Checked;
        _result.RoutePrivateNetworks = _privateRoutes.Checked;
        _result.AddCustomRoutes = _addCustomRoutes.Checked;
        _result.CustomRoutes = Ui.SplitList(_customRoutes.Text).ToList();

        _result.EnableReconnection = _reconnect.Checked;
        _result.ReconnectionCount = (int)_reconnectCount.Value;
        _result.ReconnectionIntervalSeconds = (int)_reconnectInterval.Value;

        _result.SaveLog = _saveLog.Checked;
        _result.LogFolder = _logFolder.Text.Trim();
        _result.AdapterName = string.IsNullOrWhiteSpace(_adapter.Text) ? "OpenSSTP" : _adapter.Text.Trim();
    }
}
