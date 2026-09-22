using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Spotifly.WallpaperHost;

internal sealed record WallpaperProject(
    string Id,
    string Title,
    string Type,
    string RootPath,
    string EntryFile,
    string? PreviewFile,
    bool SupportsAudio,
    Dictionary<string, object?> Properties)
{
    public bool IsSupported => Type is "video" or "web" or "scene";
}

internal sealed class WallpaperCatalog
{
    private const string WorkshopAppId = "431960";
    private readonly object _sync = new();
    private Dictionary<string, WallpaperProject> _projects = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<WallpaperProject> Projects
    {
        get
        {
            lock (_sync)
            {
                return _projects.Values
                    .OrderByDescending(project => project.IsSupported)
                    .ThenBy(project => project.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }
        }
    }

    public WallpaperProject? Find(string id)
    {
        lock (_sync)
        {
            return _projects.GetValueOrDefault(id);
        }
    }

    public void Refresh()
    {
        var projects = new Dictionary<string, WallpaperProject>(StringComparer.OrdinalIgnoreCase);

        foreach (string library in FindSteamLibraries())
        {
            string workshop = Path.Combine(library, "steamapps", "workshop", "content", WorkshopAppId);
            if (!Directory.Exists(workshop))
            {
                continue;
            }

            foreach (string projectDirectory in Directory.EnumerateDirectories(workshop))
            {
                WallpaperProject? project = TryReadProject(projectDirectory);
                if (project is not null)
                {
                    projects[project.Id] = project;
                }
            }
        }

        lock (_sync)
        {
            _projects = projects;
        }
    }

    private static WallpaperProject? TryReadProject(string projectDirectory)
    {
        string metadataPath = Path.Combine(projectDirectory, "project.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            JsonElement root = document.RootElement;
            string id = Path.GetFileName(projectDirectory);
            string type = GetString(root, "type").ToLowerInvariant();
            string title = GetString(root, "title");
            string entry = NormalizeRelativePath(GetString(root, "file"));
            string? preview = NormalizeRelativePath(GetString(root, "preview"));
            bool supportsAudio = false;
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (TryGetProperty(root, "general", out JsonElement general))
            {
                supportsAudio = GetBoolean(general, "supportsaudioprocessing");
                if (TryGetProperty(general, "properties", out JsonElement propertyRoot) && propertyRoot.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in propertyRoot.EnumerateObject())
                    {
                        if (TryGetProperty(property.Value, "value", out JsonElement value))
                        {
                            properties[property.Name] = ToObject(value);
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"Wallpaper {id}";
            }

            if (type is "video" or "web")
            {
                string? entryPath = SafeCombine(projectDirectory, entry);
                if (string.IsNullOrWhiteSpace(entry) || entryPath is null || !File.Exists(entryPath))
                {
                    type = "broken";
                }
            }

            if (!string.IsNullOrWhiteSpace(preview))
            {
                string? previewPath = SafeCombine(projectDirectory, preview);
                if (previewPath is null || !File.Exists(previewPath))
                {
                    preview = null;
                }
            }

            return new WallpaperProject(id, title, type, projectDirectory, entry, preview, supportsAudio, properties);
        }
        catch
        {
            return null;
        }
    }

    public static string? SafeCombine(string root, string relativePath)
    {
        try
        {
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(rootFull, NormalizeRelativePath(relativePath)));
            return candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    internal static IEnumerable<string> FindSteamLibraries()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfDirectory(libraries, @"C:\Program Files (x86)\Steam");
        AddIfDirectory(libraries, @"C:\Program Files\Steam");

        foreach ((RegistryHive hive, RegistryView view, string keyPath) in new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\WOW6432Node\Valve\Steam"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Valve\Steam")
        })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = baseKey.OpenSubKey(keyPath);
                string? path = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                AddIfDirectory(libraries, path);
            }
            catch
            {
            }
        }

        foreach (string steamRoot in libraries.ToArray())
        {
            string libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            try
            {
                string contents = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(contents, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                {
                    AddIfDirectory(libraries, match.Groups["path"].Value.Replace("\\\\", "\\"));
                }
            }
            catch
            {
            }
        }

        return libraries;
    }

    private static void AddIfDirectory(HashSet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            if (Directory.Exists(fullPath))
            {
                paths.Add(fullPath);
            }
        }
        catch
        {
        }
    }

    private static string NormalizeRelativePath(string? path) =>
        (path ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private static string GetString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static object? ToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => JsonSerializer.Deserialize<object>(value.GetRawText())
    };
}
