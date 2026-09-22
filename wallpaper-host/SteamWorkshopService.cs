using System.Runtime.InteropServices;
using System.Text.Json;
using Steamworks;

namespace Spotifly.WallpaperHost;

internal sealed record WorkshopItem(
    string Id,
    string Title,
    string? PreviewUrl,
    ulong FileSize,
    bool Available,
    string State,
    ulong Downloaded,
    ulong Total);

internal sealed class SteamWorkshopService : IAsyncDisposable
{
    private const string AppId = "431960";
    private readonly object _sync = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, (string Title, string? Preview, ulong Size, bool Available)> _metadata = new();
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);
    private readonly Task _callbackLoop;
    private nint _nativeLibrary;
    private bool _initialized;
    private string? _activeDownload;

    public SteamWorkshopService()
    {
        _ = TryInitialize();
        _callbackLoop = Task.Run(CallbackLoopAsync);
    }

    public bool IsAvailable => _initialized;

    public async Task<IReadOnlyList<WorkshopItem>> GetItemsAsync(bool refreshMetadata, IReadOnlySet<string> installedIds, CancellationToken cancellationToken)
    {
        if (!TryInitialize()) return Array.Empty<WorkshopItem>();
        PublishedFileId_t[] ids;
        lock (_sync)
        {
            uint count = SteamUGC.GetNumSubscribedItems();
            ids = new PublishedFileId_t[count];
            uint actual = SteamUGC.GetSubscribedItems(ids, count);
            if (actual < ids.Length) Array.Resize(ref ids, (int)actual);
        }

        string[] missing;
        lock (_sync)
        {
            missing = ids.Select(id => id.m_PublishedFileId.ToString())
                .Where(id => !installedIds.Contains(id) && (refreshMetadata || !_metadata.ContainsKey(id)))
                .ToArray();
        }
        if (missing.Length > 0)
        {
            await LoadMetadataAsync(missing, cancellationToken);
        }

        var items = new List<WorkshopItem>(ids.Length);
        lock (_sync)
        {
            foreach (PublishedFileId_t publishedId in ids)
            {
                string id = publishedId.m_PublishedFileId.ToString();
                EItemState state = (EItemState)SteamUGC.GetItemState(publishedId);
                if (_activeDownload == id &&
                    !state.HasFlag(EItemState.k_EItemStateDownloading) &&
                    !state.HasFlag(EItemState.k_EItemStateDownloadPending) &&
                    state.HasFlag(EItemState.k_EItemStateInstalled))
                {
                    _activeDownload = null;
                }
                ulong downloaded = 0;
                ulong total = 0;
                _ = SteamUGC.GetItemDownloadInfo(publishedId, out downloaded, out total);
                (string Title, string? Preview, ulong Size, bool Available) info = _metadata.GetValueOrDefault(id, ($"Wallpaper {id}", null, 0, true));
                string stateName = info.Available ? StateName(id, state) : "unavailable";
                items.Add(new WorkshopItem(id, info.Title, info.Preview, info.Size, info.Available, stateName, downloaded, total));
            }
        }
        return items;
    }

    public bool StartDownload(string id)
    {
        if (!TryInitialize() || !ulong.TryParse(id, out ulong value)) return false;
        lock (_sync)
        {
            if (_activeDownload is not null && _activeDownload != id)
            {
                if (ulong.TryParse(_activeDownload, out ulong activeValue))
                {
                    EItemState activeState = (EItemState)SteamUGC.GetItemState(new PublishedFileId_t(activeValue));
                    if (activeState.HasFlag(EItemState.k_EItemStateDownloading) ||
                        activeState.HasFlag(EItemState.k_EItemStateDownloadPending)) return false;
                }
                _activeDownload = null;
            }
            SteamUGC.SuspendDownloads(false);
            _cancelled.Remove(id);
            bool started = SteamUGC.DownloadItem(new PublishedFileId_t(value), false);
            if (started) _activeDownload = id;
            return started;
        }
    }

    public bool CancelDownload(string id)
    {
        if (!_initialized) return false;
        lock (_sync)
        {
            if (_activeDownload != id) return false;
            SteamUGC.SuspendDownloads(true);
            _cancelled.Add(id);
            _activeDownload = null;
            return true;
        }
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetPreviewAsync(string id, CancellationToken cancellationToken)
    {
        (string Title, string? Preview, ulong Size, bool Available) info;
        lock (_sync)
        {
            if (!_metadata.TryGetValue(id, out info) || string.IsNullOrWhiteSpace(info.Preview)) return null;
        }
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(info.Preview, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            string type = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return (bytes, type);
        }
        catch { return null; }
    }

    private async Task LoadMetadataAsync(string[] ids, CancellationToken cancellationToken)
    {
        foreach (string[] batch in ids.Chunk(100))
        {
            try
            {
                var values = new List<KeyValuePair<string, string>>(batch.Length + 1)
                {
                    new("itemcount", batch.Length.ToString())
                };
                for (int index = 0; index < batch.Length; index++)
                {
                    values.Add(new($"publishedfileids[{index}]", batch[index]));
                }
                using var content = new FormUrlEncodedContent(values);
                using HttpResponseMessage response = await _http.PostAsync(
                    "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", content, cancellationToken);
                response.EnsureSuccessStatusCode();
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                JsonElement details = document.RootElement.GetProperty("response").GetProperty("publishedfiledetails");
                lock (_sync)
                {
                    foreach (JsonElement detail in details.EnumerateArray())
                    {
                        string id = detail.GetProperty("publishedfileid").GetString() ?? string.Empty;
                        if (id.Length == 0) continue;
                        bool available = !detail.TryGetProperty("result", out JsonElement resultElement) || resultElement.GetInt32() == 1;
                        string title = detail.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() ?? $"Wallpaper {id}" : $"Wallpaper {id}";
                        if (!available) title = $"Недоступные обои #{id}";
                        string? preview = detail.TryGetProperty("preview_url", out JsonElement previewElement) ? previewElement.GetString() : null;
                        ulong size = 0;
                        if (detail.TryGetProperty("file_size", out JsonElement sizeElement))
                        {
                            if (sizeElement.ValueKind == JsonValueKind.Number) _ = sizeElement.TryGetUInt64(out size);
                            else if (sizeElement.ValueKind == JsonValueKind.String) _ = ulong.TryParse(sizeElement.GetString(), out size);
                        }
                        _metadata[id] = (title, preview, size, available);
                    }
                }
            }
            catch
            {
                lock (_sync)
                {
                    foreach (string id in batch) _metadata.TryAdd(id, ($"Wallpaper {id}", null, 0, true));
                }
            }
        }
    }

    private string StateName(string id, EItemState state)
    {
        if (_cancelled.Contains(id)) return "cancelled";
        if (state.HasFlag(EItemState.k_EItemStateDownloading)) return "downloading";
        if (state.HasFlag(EItemState.k_EItemStateDownloadPending)) return "pending";
        if (state.HasFlag(EItemState.k_EItemStateNeedsUpdate)) return "needsUpdate";
        if (state.HasFlag(EItemState.k_EItemStateInstalled)) return "installed";
        return "subscribed";
    }

    private async Task CallbackLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                if (!_initialized) continue;
                lock (_sync)
                {
                    try { SteamAPI.RunCallbacks(); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool TryInitialize()
    {
        lock (_sync)
        {
            if (_initialized) return true;
            try
            {
                if (_nativeLibrary == 0)
                {
                    string? api = FindSteamApi();
                    if (api is null) return false;
                    _nativeLibrary = NativeLibrary.Load(api);
                }
                Environment.SetEnvironmentVariable("SteamAppId", AppId);
                Environment.SetEnvironmentVariable("SteamGameId", AppId);
                _initialized = SteamAPI.Init();
            }
            catch { _initialized = false; }
            return _initialized;
        }
    }

    private static string? FindSteamApi()
    {
        foreach (string library in WallpaperCatalog.FindSteamLibraries())
        {
            string path = Path.Combine(library, "steamapps", "common", "wallpaper_engine", "bin", "steam_api64.dll");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { await _callbackLoop; } catch { }
        lock (_sync)
        {
            if (_initialized)
            {
                try { SteamUGC.SuspendDownloads(false); } catch { }
                try { SteamAPI.Shutdown(); } catch { }
                _initialized = false;
            }
        }
        _http.Dispose();
        _shutdown.Dispose();
        if (_nativeLibrary != 0)
        {
            try { NativeLibrary.Free(_nativeLibrary); } catch { }
        }
    }
}
