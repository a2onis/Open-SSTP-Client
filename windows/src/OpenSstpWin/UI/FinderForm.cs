using OpenSstpWin.Core;

namespace OpenSstpWin.UI;

/// <summary>
/// The Windows counterpart of the Android "Find fastest from sources" screen:
/// manage HTTPS list URLs, download them on request, probe every advertised
/// endpoint over TCP, and rank the reachable ones by connect time.
/// </summary>
internal sealed class FinderForm : Form
{
    private readonly Settings _settings;
    private readonly ProfileStore _store;
    private readonly string _theme;
    private readonly ListBox _sources = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListView _results = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        Dock = DockStyle.Fill,
    };
    private readonly Label _progress = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _compare = new() { Text = "Compare sources", AutoSize = true, Margin = new Padding(4) };
    private readonly Button _addSource = new() { Text = "Add source…", AutoSize = true, Margin = new Padding(4) };
    private readonly Button _editSource = new() { Text = "Edit…", AutoSize = true, Margin = new Padding(4) };
    private readonly Button _removeSource = new() { Text = "Remove", AutoSize = true, Margin = new Padding(4) };
    private readonly Button _saveSelected = new() { Text = "Save selected as configuration", AutoSize = true, Margin = new Padding(4) };
    private readonly Button _useSelected = new() { Text = "Use selected", AutoSize = true, Margin = new Padding(4) };
    private CancellationTokenSource? _cts;
    private bool _busy;

    internal FinderForm(Settings current, ProfileStore store, string theme)
    {
        _settings = current.Clone();
        _store = store;
        _theme = theme;

        Text = "Find fastest from sources";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(700, 520);
        ShowInTaskbar = false;

        foreach (var url in _settings.SourceUrls)
        {
            _sources.Items.Add(url);
        }

        _results.Columns.Add("#", 50, HorizontalAlignment.Right);
        _results.Columns.Add("Endpoint", 420);
        _results.Columns.Add("TCP connect", 160);
        _results.DoubleClick += (_, _) => UseSelected();

        _compare.Click += CompareClicked;
        _addSource.Click += (_, _) => AddSource();
        _editSource.Click += (_, _) => EditSource();
        _removeSource.Click += (_, _) => RemoveSource();
        _saveSelected.Click += (_, _) => SaveSelected();
        _useSelected.Click += (_, _) => UseSelected();
        var close = new Button { Text = "Close", AutoSize = true, Margin = new Padding(4) };
        close.Click += (_, _) => Close();

        var sourcesBox = new GroupBox { Text = "Source lists (HTTPS)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        sourcesBox.Controls.Add(_sources);

        var sourceButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        sourceButtons.Controls.AddRange(new Control[] { _addSource, _editSource, _removeSource });

        var compareRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        compareRow.Controls.Add(_compare);
        compareRow.Controls.Add(_progress);

        var resultsBox = new GroupBox
        {
            Text = "Results (sorted by TCP connect time)",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
        };
        resultsBox.Controls.Add(_results);

        var resultButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        resultButtons.Controls.AddRange(new Control[] { _saveSelected, _useSelected, close });

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(Intro(), 0, 0);
        root.Controls.Add(sourcesBox, 0, 1);
        root.Controls.Add(sourceButtons, 0, 2);
        root.Controls.Add(compareRow, 0, 3);
        root.Controls.Add(Hint(), 0, 4);
        root.Controls.Add(resultsBox, 0, 5);
        root.Controls.Add(resultButtons, 0, 6);
        Controls.Add(root);

        FormClosing += (_, _) => _cts?.Cancel();
        Theme.Apply(this, theme);
        SetBusyUi();
    }

    internal Settings? AppliedSettings { get; private set; }

    private static Label Intro() => new()
    {
        Text = "Downloads each list only when you compare, deduplicates up to " +
               SourceFinder.MaxEndpoints + " endpoints, and tries a TCP connection to each advertised port.",
        AutoSize = true,
        Margin = new Padding(3, 2, 3, 8),
    };

    private static Label Hint() => new()
    {
        Text = "TCP connect time includes name resolution; it does not prove SSTP or the credentials work. " +
               "Only test endpoints you are authorized to contact.",
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 2, 3, 8),
    };

    private List<string> Sources() => _sources.Items.Cast<object>()
        .Select(item => item.ToString() ?? string.Empty)
        .Where(value => value.Length > 0)
        .ToList();

    private void PersistSources()
    {
        var urls = Sources();
        _settings.SourceUrls = urls.ToList();
        try
        {
            // Write only the source list into the stored settings; other unsaved
            // form edits must not leak into settings.json from here.
            var stored = Settings.Load();
            stored.SourceUrls = urls.ToList();
            stored.Save();
            _progress.Text = "Source list saved.";
        }
        catch (Exception exception)
        {
            _progress.Text = "Could not save the source list: " + exception.Message;
        }
    }

    private void AddSource()
    {
        var value = PromptForm.Show(this, "Add source", "HTTPS list URL", "https://", _theme);
        if (value is null)
        {
            return;
        }

        if (!SourceFinder.IsValidSourceUrl(value))
        {
            MessageBox.Show(this, "Enter an HTTPS URL without credentials.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (Sources().Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That source is already in the list.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _sources.Items.Add(value);
        PersistSources();
    }

    private void EditSource()
    {
        if (_sources.SelectedItem is not string current)
        {
            return;
        }

        var value = PromptForm.Show(this, "Edit source", "HTTPS list URL", current, _theme);
        if (value is null)
        {
            return;
        }

        if (!SourceFinder.IsValidSourceUrl(value))
        {
            MessageBox.Show(this, "Enter an HTTPS URL without credentials.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (Sources().Any(existing =>
                !string.Equals(existing, current, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That source is already in the list.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var index = _sources.SelectedIndex;
        _sources.Items[index] = value;
        PersistSources();
    }

    private void RemoveSource()
    {
        if (_sources.SelectedIndex < 0)
        {
            return;
        }

        _sources.Items.RemoveAt(_sources.SelectedIndex);
        PersistSources();
    }

    private async void CompareClicked(object? sender, EventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            return;
        }

        var sources = Sources();
        if (sources.Count == 0)
        {
            MessageBox.Show(this, "Add a source URL first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (sources.Any(source => !SourceFinder.IsValidSourceUrl(source)))
        {
            MessageBox.Show(this, "Every source must be an HTTPS URL without credentials.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirmation =
            "Download " + sources.Count + " list(s) and try a TCP connection to each advertised " +
            "host and port, up to " + SourceFinder.MaxEndpoints + " unique endpoints?\n\n" +
            "Only test servers you are authorized to contact. TCP timing does not verify SSTP or login.";
        if (MessageBox.Show(this, confirmation, "Compare endpoints?", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        PersistSources();
        _busy = true;
        SetBusyUi();
        _results.Items.Clear();
        _progress.Text = "Downloading lists…";
        _cts = new CancellationTokenSource();

        try
        {
            var outcome = await new SourceFinder().CompareAsync(
                sources,
                new Progress<FinderProgress>(ShowProgress),
                _cts.Token);

            if (IsDisposed)
            {
                return;
            }

            ShowResults(outcome);
            var summary = outcome.Results.Count + "/" + outcome.TotalEndpoints + " reachable";
            if (outcome.FailedSources.Count > 0)
            {
                summary += " · " + outcome.FailedSources.Count + " source(s) failed";
            }

            _progress.Text = summary;
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                _progress.Text = "Comparison cancelled";
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                _progress.Text = "Comparison failed";
                MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _busy = false;
            if (!IsDisposed)
            {
                SetBusyUi();
            }
        }
    }

    private void ShowProgress(FinderProgress progress)
    {
        if (IsDisposed)
        {
            return;
        }

        _progress.Text = "Tested " + progress.Tested + "/" + progress.Total + " · reachable " + progress.Reachable;
    }

    private void ShowResults(FinderOutcome outcome)
    {
        _results.BeginUpdate();
        _results.Items.Clear();
        for (var index = 0; index < outcome.Results.Count; index++)
        {
            var result = outcome.Results[index];
            var item = new ListViewItem((index + 1).ToString())
            {
                Tag = result.Endpoint,
            };
            item.SubItems.Add(result.Endpoint.DisplayName);
            item.SubItems.Add(result.LatencyMs + " ms");
            _results.Items.Add(item);
        }

        _results.EndUpdate();

        if (_results.Items.Count > 0)
        {
            _results.Items[0].Selected = true;
            _results.Items[0].Focused = true;
        }
    }

    private void SetBusyUi()
    {
        _compare.Text = _busy ? "Cancel comparison" : "Compare sources";
        _sources.Enabled = !_busy;
        _results.Enabled = !_busy;
        foreach (var control in new Control[] { _addSource, _editSource, _removeSource, _saveSelected, _useSelected })
        {
            control.Enabled = !_busy;
        }
    }

    private Endpoint? SelectedEndpoint() =>
        _results.SelectedItems.Count > 0 && _results.SelectedItems[0].Tag is Endpoint endpoint
            ? endpoint
            : null;

    private void SaveSelected()
    {
        var endpoint = SelectedEndpoint();
        if (endpoint is null)
        {
            MessageBox.Show(this, "Select a result first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var name = endpoint.DisplayName;
        if (_store.Find(name) is not null &&
            MessageBox.Show(this, "A configuration named \"" + name + "\" already exists. Replace it?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var settings = new Settings { Host = endpoint.Host, Port = endpoint.Port };
        _store.AddOrUpdate(name, settings);
        MessageBox.Show(this, "Saved \"" + name + "\". Edit the credentials in Configurations before connecting.", Text,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UseSelected()
    {
        var endpoint = SelectedEndpoint();
        if (endpoint is null)
        {
            MessageBox.Show(this, "Select a result first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var applied = _settings.Clone();
        applied.Host = endpoint.Host;
        applied.Port = endpoint.Port;
        AppliedSettings = applied;
        DialogResult = DialogResult.OK;
        Close();
    }
}
