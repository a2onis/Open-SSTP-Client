using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSstpWin.Core;

/// <summary>A named snapshot of every connection setting, like the Android profiles.</summary>
public sealed class Profile
{
    public string Name { get; set; } = string.Empty;
    public Settings Settings { get; set; } = new();
}

/// <summary>
/// Reads and writes %LOCALAPPDATA%\OpenSstpWin\profiles.json. Credentials in a
/// saved profile are plaintext, exactly as they are in the Android app.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ProfileStore(string path)
    {
        _path = path;
        Load();
    }

    public static ProfileStore CreateDefault() => new(Settings.ProfilesPath);

    public string FilePath => _path;

    /// <summary>Set when the existing file could not be read; the file is then preserved aside.</summary>
    public string? LoadError { get; private set; }

    public List<Profile> Profiles { get; } = new();

    /// <summary>Profiles sorted by name, which is how the Android list shows them.</summary>
    public IReadOnlyList<Profile> All => Profiles
        .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public Profile? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds a profile or updates an existing one. When <paramref name="existingName"/>
    /// is supplied the entry is renamed; a clash with a different profile fails.
    /// </summary>
    public bool AddOrUpdate(string name, Settings settings, string? existingName = null, bool save = true)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new ArgumentException("A configuration name is required.", nameof(name));
        }

        var current = Find(existingName) ?? Find(name);
        var clash = Find(name);
        if (clash is not null && (current is null || !ReferenceEquals(clash, current)))
        {
            return false;
        }

        if (current is null)
        {
            Profiles.Add(new Profile { Name = name, Settings = settings.Clone() });
        }
        else
        {
            current.Name = name;
            current.Settings = settings.Clone();
        }

        if (save)
        {
            Save();
        }

        return true;
    }

    public bool Delete(string name, bool save = true)
    {
        var profile = Find(name);
        if (profile is null)
        {
            return false;
        }

        Profiles.Remove(profile);
        if (save)
        {
            Save();
        }

        return true;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Profiles, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }

    private void Load()
    {
        Profiles.Clear();
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(_path), JsonOptions);
            if (loaded is null)
            {
                return;
            }

            foreach (var profile in loaded)
            {
                if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
                {
                    continue;
                }

                profile.Settings ??= new Settings();
                profile.Settings.Normalize();
                if (Profiles.All(existing =>
                        !string.Equals(existing.Name, profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    profile.Name = profile.Name.Trim();
                    Profiles.Add(profile);
                }
            }
        }
        catch (Exception exception)
        {
            LoadError = exception.Message;

            // Keep the unreadable file instead of overwriting it on the next save.
            try
            {
                File.Move(_path, _path + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            }
            catch
            {
                // Best effort only.
            }
        }
    }
}

/// <summary>Import/export of a single settings snapshot (the Android "Profile" JSON).</summary>
public static class SettingsFile
{
    public static void Export(Settings settings, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, settings.ToJson());
    }

    public static Settings Import(string path) => Settings.FromJson(File.ReadAllText(path));
}
