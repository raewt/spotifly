using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Spotifly.WallpaperHost;

internal sealed class LocalServer : IAsyncDisposable
{
    public const int Port = 17654;
    public const string ApiToken = "spotifly-wallpaper-v1";
    private readonly WallpaperCatalog _catalog;
    private readonly AudioSpectrumService _audio;
    private readonly SceneCaptureService _scenes;
    private readonly SteamWorkshopService _workshop;
    private readonly Func<System.Diagnostics.Process?> _launchClient;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenLoop;

    public LocalServer(WallpaperCatalog catalog, AudioSpectrumService audio, SceneCaptureService scenes, SteamWorkshopService workshop, Func<System.Diagnostics.Process?> launchClient)
    {
        _catalog = catalog;
        _audio = audio;
        _scenes = scenes;
        _workshop = workshop;
        _launchClient = launchClient;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.Register(_shutdown.Cancel);
        _listener.Start();
        _listenLoop = Task.Run(ListenLoopAsync);
        return Task.CompletedTask;
    }

    private async Task ListenLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                _ = Task.Run(() => HandleAsync(context));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            AddCommonHeaders(context.Response);
            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            string path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/audio", StringComparison.OrdinalIgnoreCase) && context.Request.IsWebSocketRequest)
            {
                if (!IsAuthorized(context.Request))
                {
                    await SendStatusAsync(context.Response, 403);
                    return;
                }
                WebSocketContext webSocket = await context.AcceptWebSocketAsync(null);
                await _audio.AddClientAsync(webSocket.WebSocket, _shutdown.Token);
                return;
            }

            if (path.Equals("/bridge.js", StringComparison.OrdinalIgnoreCase))
            {
                await SendTextAsync(context.Response, WebWallpaperBridge.Script, "application/javascript; charset=utf-8");
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAuthorized(context.Request))
                {
                    await SendStatusAsync(context.Response, 403);
                    return;
                }
                await HandleApiAsync(context, path);
                return;
            }

            if (path.StartsWith("/wallpaper/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWallpaperFileAsync(context, path);
                return;
            }

            if (path.StartsWith("/workshop/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWorkshopAssetAsync(context, path);
                return;
            }

            await SendStatusAsync(context.Response, 404);
        }
        catch
        {
            try { await SendStatusAsync(context.Response, 500); } catch { }
        }
    }

    private async Task HandleApiAsync(HttpListenerContext context, string path)
    {
        if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await SendJsonAsync(context.Response, new
            {
                ready = true,
                version = "1",
                projects = _catalog.Projects.Count,
                supported = _catalog.Projects.Count(project => project.IsSupported),
                audioClients = _audio.ClientCount,
                audioCapturing = _audio.IsCapturing,
                visible = _audio.IsVisible,
                steamAvailable = _workshop.IsAvailable
            });
            return;
        }

        if (path.Equals("/api/projects", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Request.QueryString["refresh"] == "1")
            {
                _catalog.Refresh();
            }

            HashSet<string> currentIds = _catalog.Projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<WorkshopItem> subscribed = await _workshop.GetItemsAsync(
                context.Request.QueryString["refresh"] == "1", currentIds, _shutdown.Token);
            if (subscribed.Any(item => item.State == "installed" && !currentIds.Contains(item.Id)))
            {
                _catalog.Refresh();
            }
            Dictionary<string, WorkshopItem> subscribedById = subscribed.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var local = _catalog.Projects.Select(project =>
            {
                subscribedById.TryGetValue(project.Id, out WorkshopItem? item);
                return new
                {
                    id = project.Id,
                    title = project.Title,
                    type = project.Type,
                    supported = project.IsSupported,
                    audio = project.SupportsAudio,
                    installed = true,
                    downloadable = false,
                    downloadState = item?.State ?? "installed",
                    downloaded = item?.Downloaded ?? 0,
                    total = item?.Total ?? 0,
                    preview = project.PreviewFile is null ? null : $"http://127.0.0.1:{Port}/wallpaper/{Uri.EscapeDataString(project.Id)}/preview",
                    entry = project.IsSupported ? BuildEntryUrl(project) : null
                };
            });
            HashSet<string> localIds = _catalog.Projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
            var remote = subscribed.Where(item => !localIds.Contains(item.Id)).Select(item => new
            {
                id = item.Id,
                title = item.Title,
                type = "remote",
                supported = false,
                audio = false,
                installed = false,
                downloadable = item.Available,
                downloadState = item.State,
                downloaded = item.Downloaded,
                total = item.Total > 0 ? item.Total : item.FileSize,
                preview = item.PreviewUrl is null ? null : $"http://127.0.0.1:{Port}/workshop/{Uri.EscapeDataString(item.Id)}/preview",
                entry = (string?)null
            });
            await SendJsonAsync(context.Response, new { projects = local.Concat(remote), steamAvailable = _workshop.IsAvailable });
            return;
        }

        if (path.StartsWith("/api/download/", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
        {
            string id = Uri.UnescapeDataString(path["/api/download/".Length..]);
            bool cancel = context.Request.QueryString["cancel"] == "1";
            bool accepted = cancel ? _workshop.CancelDownload(id) : _workshop.StartDownload(id);
            await SendJsonAsync(context.Response, new { accepted, id, action = cancel ? "cancel" : "download" });
            return;
        }

        if (path.Equals("/api/activity", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.InputStream);
            bool visible = document.RootElement.TryGetProperty("visible", out JsonElement value) && value.ValueKind == JsonValueKind.True;
            string? sceneId = document.RootElement.TryGetProperty("sceneId", out JsonElement sceneValue) && sceneValue.ValueKind == JsonValueKind.String
                ? sceneValue.GetString()
                : null;
            WallpaperProject? scene = sceneId is null ? null : _catalog.Find(sceneId);
            if (scene?.Type != "scene") scene = null;
            _audio.SetVisible(visible);
            await _scenes.SetActivityAsync(visible, scene);
            await SendStatusAsync(context.Response, 204);
            return;
        }

        if (path.Equals("/api/launch", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
        {
            _launchClient();
            await SendStatusAsync(context.Response, 204);
            return;
        }

        await SendStatusAsync(context.Response, 404);
    }

    private async Task HandleWorkshopAssetAsync(HttpListenerContext context, string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[2].Equals("preview", StringComparison.OrdinalIgnoreCase))
        {
            await SendStatusAsync(context.Response, 404);
            return;
        }
        var preview = await _workshop.GetPreviewAsync(Uri.UnescapeDataString(parts[1]), _shutdown.Token);
        if (preview is null)
        {
            await SendStatusAsync(context.Response, 404);
            return;
        }
        context.Response.ContentType = preview.Value.ContentType;
        context.Response.Headers["Cache-Control"] = "public, max-age=3600";
        context.Response.ContentLength64 = preview.Value.Bytes.Length;
        await context.Response.OutputStream.WriteAsync(preview.Value.Bytes, _shutdown.Token);
        context.Response.Close();
    }

    private async Task HandleWallpaperFileAsync(HttpListenerContext context, string path)
    {
        string[] parts = path.Split('/', 5, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await SendStatusAsync(context.Response, 404);
            return;
        }

        string id = Uri.UnescapeDataString(parts[1]);
        string action = parts[2];
        WallpaperProject? project = _catalog.Find(id);
        if (project is null)
        {
            await SendStatusAsync(context.Response, 404);
            return;
        }

        if (action.Equals("player", StringComparison.OrdinalIgnoreCase) && project.Type == "video")
        {
            await SendVideoPlayerAsync(context.Response, project);
            return;
        }

        if (action.Equals("scene", StringComparison.OrdinalIgnoreCase) && project.Type == "scene")
        {
            await SendScenePlayerAsync(context.Response, project);
            return;
        }

        if (action.Equals("scene-stream", StringComparison.OrdinalIgnoreCase) && project.Type == "scene")
        {
            await SendSceneStreamAsync(context, project);
            return;
        }

        string relativePath;
        if (action.Equals("preview", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = project.PreviewFile ?? string.Empty;
        }
        else if (action.Equals("files", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
        {
            relativePath = Uri.UnescapeDataString(parts.Length == 4 ? parts[3] : parts[3] + "/" + parts[4]);
        }
        else
        {
            await SendStatusAsync(context.Response, 404);
            return;
        }

        string? filePath = WallpaperCatalog.SafeCombine(project.RootPath, relativePath);
        if (filePath is null || !File.Exists(filePath))
        {
            await SendStatusAsync(context.Response, 404);
            return;
        }

        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        context.Response.ContentType = MimeType(extension);
        context.Response.Headers["Cache-Control"] = extension is ".html" or ".htm" ? "no-store" : "public, max-age=3600";

        if (extension is ".html" or ".htm")
        {
            string html = await File.ReadAllTextAsync(filePath);
            string configuration = JsonSerializer.Serialize(project.Properties, new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.Default
            });
            string injection = $"<script>window.__spotiflyProjectProperties={configuration};</script><script src=\"http://127.0.0.1:{Port}/bridge.js\"></script>";
            int head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
            if (head >= 0)
            {
                int close = html.IndexOf('>', head);
                html = close >= 0 ? html.Insert(close + 1, injection) : injection + html;
            }
            else
            {
                html = injection + html;
            }
            await SendTextAsync(context.Response, html, context.Response.ContentType);
            return;
        }

        await SendFileAsync(context, filePath);
    }

    private async Task SendFileAsync(HttpListenerContext context, string filePath)
    {
        var file = new FileInfo(filePath);
        if (file.Length == 0)
        {
            context.Response.ContentLength64 = 0;
            context.Response.Close();
            return;
        }
        long start = 0;
        long end = file.Length - 1;
        string? range = context.Request.Headers["Range"];
        context.Response.Headers["Accept-Ranges"] = "bytes";

        if (!string.IsNullOrWhiteSpace(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            string[] bounds = range[6..].Split(',', 2)[0].Split('-', 2);
            if (bounds.Length == 2 && long.TryParse(bounds[0], out long requestedStart))
            {
                start = Math.Clamp(requestedStart, 0, file.Length - 1);
                if (long.TryParse(bounds[1], out long requestedEnd))
                {
                    end = Math.Clamp(requestedEnd, start, file.Length - 1);
                }
                context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{file.Length}";
            }
        }

        long remaining = end - start + 1;
        context.Response.ContentLength64 = remaining;
        if (context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Close();
            return;
        }

        await using Stream output = context.Response.OutputStream;
        await using FileStream input = file.OpenRead();
        input.Position = start;
        byte[] buffer = new byte[128 * 1024];
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), _shutdown.Token);
            if (read <= 0)
            {
                break;
            }
            await output.WriteAsync(buffer.AsMemory(0, read), _shutdown.Token);
            remaining -= read;
        }
    }

    private static string BuildEntryUrl(WallpaperProject project)
    {
        if (project.Type == "video")
        {
            return $"http://127.0.0.1:{Port}/wallpaper/{Uri.EscapeDataString(project.Id)}/player";
        }
        if (project.Type == "scene")
        {
            return $"http://127.0.0.1:{Port}/wallpaper/{Uri.EscapeDataString(project.Id)}/scene";
        }

        string escapedPath = string.Join('/', project.EntryFile
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return $"http://127.0.0.1:{Port}/wallpaper/{Uri.EscapeDataString(project.Id)}/files/{escapedPath}";
    }

    private static async Task SendVideoPlayerAsync(HttpListenerResponse response, WallpaperProject project)
    {
        string escapedPath = string.Join('/', project.EntryFile
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        string source = $"/wallpaper/{Uri.EscapeDataString(project.Id)}/files/{escapedPath}";
        string sourceJson = JsonSerializer.Serialize(source);
        string html = $$"""
<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<style>
html,body{width:100%;height:100%;margin:0;overflow:hidden;background:transparent}
video{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;object-position:50% 50%;background:transparent}
</style></head><body>
<video id="wall" muted loop playsinline preload="auto"></video>
<script>
(()=>{
  const video=document.getElementById('wall');
  let active=true;
  const mute=()=>{video.muted=true;video.defaultMuted=true;video.volume=0};
  const sync=()=>{mute();if(active&&!document.hidden)video.play().catch(()=>{});else video.pause()};
  video.src={{sourceJson}};
  video.addEventListener('volumechange',mute);
  video.addEventListener('canplay',()=>{sync();parent.postMessage({type:'spotifly:wallpaper-ready'},'*')},{once:true});
  video.addEventListener('error',()=>parent.postMessage({type:'spotifly:wallpaper-error'},'*'));
  addEventListener('message',event=>{
    const data=event.data||{};
    if(data.type==='spotifly:visibility'){active=!!data.visible;sync()}
    if(data.type==='spotifly:layout'){
      video.style.objectFit=data.fit==='contain'?'contain':data.fit==='fill'?'fill':'cover';
      video.style.objectPosition=(Number(data.x)||50)+'% '+(Number(data.y)||50)+'%';
    }
  });
  document.addEventListener('visibilitychange',sync);
  mute();sync();
})();
</script></body></html>
""";
        response.Headers["Cache-Control"] = "no-store";
        await SendTextAsync(response, html, "text/html; charset=utf-8");
    }

    private static async Task SendScenePlayerAsync(HttpListenerResponse response, WallpaperProject project)
    {
        string stream = $"/wallpaper/{Uri.EscapeDataString(project.Id)}/scene-stream";
        string streamJson = JsonSerializer.Serialize(stream);
        string html = $$"""
<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<style>
html,body{width:100%;height:100%;margin:0;overflow:hidden;background:transparent}
img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;object-position:50% 50%;background:transparent}
</style></head><body><img id="wall" alt="">
<script>
(()=>{
 const image=document.getElementById('wall'),source={{streamJson}};let active=true;
 const sync=()=>{if(active&&!document.hidden){if(!image.src)image.src=source+'?v='+Date.now()} else {image.removeAttribute('src')} };
 image.addEventListener('load',()=>parent.postMessage({type:'spotifly:wallpaper-ready'},'*'));
 image.addEventListener('error',()=>{if(active)setTimeout(sync,1200)});
 addEventListener('message',event=>{const data=event.data||{};
   if(data.type==='spotifly:visibility'){active=!!data.visible;sync()}
   if(data.type==='spotifly:layout'){image.style.objectFit=data.fit==='contain'?'contain':data.fit==='fill'?'fill':'cover';image.style.objectPosition=(Number(data.x)||50)+'% '+(Number(data.y)||50)+'%'}
 });
 document.addEventListener('visibilitychange',sync);sync();
})();
</script></body></html>
""";
        response.Headers["Cache-Control"] = "no-store";
        await SendTextAsync(response, html, "text/html; charset=utf-8");
    }

    private async Task SendSceneStreamAsync(HttpListenerContext context, WallpaperProject project)
    {
        if (!await _scenes.EnsureStartedAsync(project, _shutdown.Token))
        {
            await SendStatusAsync(context.Response, 503);
            return;
        }

        HttpListenerResponse response = context.Response;
        response.ContentType = "multipart/x-mixed-replace; boundary=spotiflyframe";
        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        response.SendChunked = true;
        long version = -1;
        try
        {
            await using Stream output = response.OutputStream;
            while (!_shutdown.IsCancellationRequested)
            {
                version = await _scenes.WaitForFrameAsync(version, _shutdown.Token);
                byte[]? frame = _scenes.LatestFrame;
                if (frame is null) continue;
                byte[] header = Encoding.ASCII.GetBytes(
                    $"--spotiflyframe\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                await output.WriteAsync(header, _shutdown.Token);
                await output.WriteAsync(frame, _shutdown.Token);
                await output.WriteAsync("\r\n"u8.ToArray(), _shutdown.Token);
                await output.FlushAsync(_shutdown.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or HttpListenerException or OperationCanceledException)
        {
        }
    }

    private static bool IsAuthorized(HttpListenerRequest request) =>
        request.Headers["X-Spotifly-Token"] == ApiToken || request.QueryString["token"] == ApiToken;

    private static void AddCommonHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Spotifly-Token";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static async Task SendJsonAsync(HttpListenerResponse response, object value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload);
        response.Close();
    }

    private static async Task SendTextAsync(HttpListenerResponse response, string value, string contentType)
    {
        byte[] payload = Encoding.UTF8.GetBytes(value);
        response.ContentType = contentType;
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload);
        response.Close();
    }

    private static Task SendStatusAsync(HttpListenerResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        response.ContentLength64 = 0;
        response.Close();
        return Task.CompletedTask;
    }

    private static string MimeType(string extension) => extension switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream"
    };

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        if (_listenLoop is not null)
        {
            try { await _listenLoop; } catch { }
        }
        _shutdown.Dispose();
    }
}
