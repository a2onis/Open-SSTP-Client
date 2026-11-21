using System.Text;
using OpenSstpWin.Core;

namespace OpenSstpWin.UI;

/// <summary>
/// The Windows "Configurations" screen: saved profiles with apply/edit/test/delete,
/// endpoint-list import, and the entry point to "Find fastest from sources".
/// </summary>
internal sealed class ConfigurationsForm : Form
{
    private const int MaxImportEndpoints = 200;
    private const long MaxImportBytes = 1024 * 1024;

    private readonly ProfileStore _store;
    private readonly Settings _current;
    private readonly string _theme;
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        Dock = DockStyle.Fill,
    };
    private readonly Dictionary<string, string> _tests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left };

    internal ConfigurationsForm(ProfileStore store, Settings current, string theme, bool importOnOpen = false)
    {
        _store = store;
        _current = current;
        _theme = theme;

        Text = "Configurations";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 500);
        MinimumSize = new Size(680, 400);
        ShowInTaskbar = false;

        _list.Columns.Add("Name", 200);
        _list.Columns.Add("Endpoint", 230);
        _list.Columns.Add("Username", 150);
        _list.Columns.Add("Connection test", 210);
        _list.DoubleClick += (_, _) => UseSelected();

        var use = Ui.Button("Use selected", (_, _) => UseSelected());
        var edit = Ui.Button("Edit…", (_, _) => EditSelected());
        var delete = Ui.Button("Delete", (_, _) => DeleteSelected());
        var test = Ui.Button("Test connection", (_, _) => TestSelected());
        var add = Ui.Button("Add from current settings…", (_, _) => AddFromCurrent());
        var import = Ui.Button("Import endpoint list…", (_, _) => ImportEndpoints());
        var find = Ui.Button("Find fastest from sources…", (_, _) => OpenFinder());
        var close = Ui.Button("Close", (_, _) => Close());

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(8, 6, 8, 4),
        };
        actions.Controls.AddRange(new Control[] { use, edit, delete, test, add, import, find, close });
        actions.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(actions);

        Theme.Apply(this, theme);
        RefreshList();

        if (importOnOpen)
        {
            Shown += (_, _) => ImportEndpoints();
        }
    }

    internal Settings? AppliedSettings { get; private set; }

    private void RefreshList()
    {
        var selected = SelectedName();
        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var profile in _store.All)
        {
            var item = new ListViewItem(profile.Name) { Tag = profile.Name };
            item.SubItems.Add(EndpointText(profile.Settings));
            item.SubItems.Add(profile.Settings.Username);
            item.SubItems.Add(_tests.TryGetValue(profile.Name, out var result) ? result : string.Empty);
            if (string.Equals(profile.Name, selected, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
            }

            _list.Items.Add(item);
        }

        _list.EndUpdate();

        _status.Text = _store.Profiles.Count == 0
            ? "No saved configurations yet. Add one from the current settings or import a list."
            : _store.Profiles.Count + " configuration(s). Credentials are stored as plaintext.";

        if (!string.IsNullOrEmpty(_store.LoadError))
        {
            _status.Text = "The old profiles file could not be read (" + _store.LoadError +
                           "); it was kept aside and a new list was started.";
        }
    }

    private static string EndpointText(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            return "(no server)";
        }

        var host = EndpointParser.NormalizeHost(settings.Host);
        return (host.Contains(':') ? "[" + host + "]" : host) + ":" + settings.Port;
    }

    private string? SelectedName() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

    private Profile? SelectedProfile()
    {
        var name = SelectedName();
        return name is null ? null : _store.Find(name);
    }

    private void UseSelected()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            MessageBox.Show(this, "Select a configuration first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AppliedSettings = profile.Settings.Clone();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddFromCurrent()
    {
        using var dialog = new ProfileEditForm(string.Empty, _current, _theme);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SaveEdited(null, dialog);
        }
    }

    private void EditSelected()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            MessageBox.Show(this, "Select a configuration first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ProfileEditForm(profile.Name, profile.Settings, _theme);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SaveEdited(profile.Name, dialog);
        }
    }

    private void SaveEdited(string? originalName, ProfileEditForm dialog)
    {
        var name = dialog.ProfileName;
        if (!string.Equals(name, originalName, StringComparison.OrdinalIgnoreCase) &&
            _store.Find(name) is not null)
        {
            MessageBox.Show(this, "A configuration with that name already exists.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_store.AddOrUpdate(name, dialog.Result, originalName))
        {
            MessageBox.Show(this, "A configuration with that name already exists.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (originalName is not null)
        {
            _tests.Remove(originalName);
        }

        _tests.Remove(name);
        RefreshList();
    }

    private void DeleteSelected()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            return;
        }

        if (MessageBox.Show(this, "Delete the configuration \"" + profile.Name + "\"?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _store.Delete(profile.Name);
        _tests.Remove(profile.Name);
        RefreshList();
    }

    private async void TestSelected()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            MessageBox.Show(this, "Select a configuration first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _tests[profile.Name] = "Testing…";
        RefreshList();

        var result = await EndpointProbe.TestAsync(profile.Settings.Host, profile.Settings.Port);
        if (IsDisposed)
        {
            return;
        }

        if (_store.Find(profile.Name) is null)
        {
            _tests.Remove(profile.Name);
        }
        else
        {
            _tests[profile.Name] = result;
        }

        RefreshList();
    }

    internal void ImportEndpoints()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import endpoint list",
            Filter = "Endpoint lists (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string text;
        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > MaxImportBytes)
            {
                throw new InvalidOperationException("The file is larger than 1 MiB.");
            }

            text = Encoding.UTF8.GetString(File.ReadAllBytes(dialog.FileName)).TrimStart('\uFEFF');
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Could not read the list (maximum 1 MiB).\n\n" + exception.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var endpoints = EndpointParser.ParseText(text, MaxImportEndpoints, out var exceeded);
        if (exceeded)
        {
            MessageBox.Show(this,
                "The list exceeds " + MaxImportEndpoints + " unique endpoints. Split the file first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (endpoints.Count == 0)
        {
            MessageBox.Show(this, "No valid endpoints were found in the file.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var message =
            "Found " + endpoints.Count + " unique endpoints.\n\n" +
            "Existing configurations with the same endpoint name are not replaced. " +
            "Imported entries use default settings and contain no credentials. " +
            "Importing does not contact any listed host. Only test servers you are authorized to contact.\n\n" +
            "Import them now?";
        if (MessageBox.Show(this, message, "Import endpoint list?", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var added = 0;
        foreach (var endpoint in endpoints)
        {
            if (_store.Find(endpoint.DisplayName) is not null)
            {
                continue;
            }

            var settings = new Settings
            {
                Host = endpoint.Host,
                Port = endpoint.Port,
            };
            if (_store.AddOrUpdate(endpoint.DisplayName, settings, save: false))
            {
                added++;
            }
        }

        _store.Save();
        RefreshList();
        MessageBox.Show(this, "Imported " + added + " configuration(s). Edit the credentials before connecting.", Text,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenFinder()
    {
        using var finder = new FinderForm(_current, _store, _theme);
        finder.ShowDialog(this);
        if (finder.AppliedSettings is Settings applied)
        {
            AppliedSettings = applied;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        RefreshList();
    }
}
