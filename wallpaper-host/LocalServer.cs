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
    private readonly VideoPreparationService _videoPreparation;
    private readonly Func<System.Diagnostics.Process?> _launchClient;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenLoop;

    public LocalServer(WallpaperCatalog catalog, AudioSpectrumService audio, VideoPreparationService videoPreparation, Func<System.Diagnostics.Process?> launchClient)
    {
        _catalog = catalog;
        _audio = audio;
        _videoPreparation = videoPreparation;
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

            if (path.StartsWith("/import/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleImportedVideoAsync(context, path);
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
            WallpaperProject[] compatible = _catalog.Projects.Where(project => project.Type is "web" or "video").ToArray();
            await SendJsonAsync(context.Response, new
            {
                ready = true,
                version = "1.6.0",
                projects = _catalog.Projects.Count,
                compatible = compatible.Length,
                scenesSkipped = _catalog.Projects.Count(project => project.Type == "scene"),
                audioClients = _audio.ClientCount,
                audioCapturing = _audio.IsCapturing,
                visible = _audio.IsVisible,
                converterAvailable = _videoPreparation.IsAvailable
            });
            return;
        }

        if (path.Equals("/api/projects", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Request.QueryString["refresh"] == "1")
            {
                _catalog.Refresh();
            }

            int scenesSkipped = _catalog.Projects.Count(project => project.Type == "scene");
            var projects = _catalog.Projects
                .Where(project => project.Type is "web" or "video")
                .Select(project =>
            {
                VideoPreparationSnapshot? preparation = project.Type == "video"
                    ? _videoPreparation.GetProjectStatus(project)
                    : null;
                bool ready = project.Type == "web" || preparation?.State == "ready";
                return new
                {
                    id = project.Id,
                    title = project.Title,
                    type = project.Type,
                    supported = ready,
                    preparable = project.Type == "video" && preparation?.State != "ready" && _videoPreparation.IsAvailable,
                    audio = project.SupportsAudio,
                    installed = true,
                    prepareState = preparation?.State ?? "ready",
                    progress = preparation?.Progress ?? 100,
                    processed = preparation?.ProcessedMilliseconds ?? 0,
                    duration = preparation?.DurationMilliseconds ?? 0,
                    error = preparation?.Error,
                    preview = project.PreviewFile is null ? null : $"http://127.0.0.1:{Port}/wallpaper/{Uri.EscapeDataString(project.Id)}/preview",
                    entry = ready ? BuildEntryUrl(project) : null
                };
            });
            await SendJsonAsync(context.Response, new
            {
                projects,
                scenesSkipped,
                converterAvailable = _videoPreparation.IsAvailable
            });
            return;
        }

        if (path.StartsWith("/api/prepare/", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
        {
            string id = Uri.UnescapeDataString(path["/api/prepare/".Length..]);
            bool cancel = context.Request.QueryString["cancel"] == "1";
            WallpaperProject? project = _catalog.Find(id);
            bool accepted = project?.Type == "video" &&
                (cancel ? _videoPreparation.Cancel(id) : _videoPreparation.StartProject(project));
            await SendJsonAsync(context.Response, new { accepted, id, action = cancel ? "cancel" : "prepare" });
            return;
        }

        if (path.Equals("/api/import", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
        {
            string? fileName = context.Request.QueryString["name"];
            ImportedVideo? imported = await _videoPreparation.ImportAsync(context.Request.InputStream, fileName, _shutdown.Token);
            if (imported is null)
            {
                await SendJsonAsync(context.Response, new { accepted = false, error = "Видеоконвертер недоступен" });
                return;
            }
            await SendJsonAsync(context.Response, new
            {
                accepted = true,
                id = imported.Id,
                title = imported.Title,
                entry = $"http://127.0.0.1:{Port}/import/{Uri.EscapeDataString(imported.Id)}/player"
            });
            return;
        }

        if (path.StartsWith("/api/import/", StringComparison.OrdinalIgnoreCase))
        {
            string id = Uri.UnescapeDataString(path["/api/import/".Length..]);
            if (context.Request.HttpMethod == "POST" && context.Request.QueryString["cancel"] == "1")
            {
                await SendJsonAsync(context.Response, new { accepted = _videoPreparation.Cancel(id), id, action = "cancel" });
                return;
            }
            VideoPreparationSnapshot status = _videoPreparation.GetImportedStatus(id);
            ImportedVideo? imported = _videoPreparation.FindImported(id);
            await SendJsonAsync(context.Response, new
            {
                id,
                title = imported?.Title,
                state = status.State,
                progress = status.Progress,
                processed = status.ProcessedMilliseconds,
                duration = status.DurationMilliseconds,
                error = status.Error,
                entry = status.State == "ready" ? $"http://127.0.0.1:{Port}/import/{Uri.EscapeDataString(id)}/player" : null
            });
            return;
        }

        if (path.Equals("/api/window/drag/start", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
        {
            bool parsedX = int.TryParse(context.Request.QueryString["x"], out int x);
            bool parsedY = int.TryParse(context.Request.QueryString["y"], out int y);
            bool started = parsedX && parsedY && NativeWindowDrag.Begin(x, y);
            await SendJsonAsync(context.Response, new { started });
            return;
        }

        if (path.Equals("/api/window/drag/move", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
        {
            bool parsedX = int.TryParse(context.Request.QueryString["x"], out int x);
            bool parsedY = int.TryParse(context.Request.QueryString["y"], out int y);
            bool moved = parsedX && parsedY && NativeWindowDrag.Move(x, y);
            await SendJsonAsync(context.Response, new { moved });
            return;
        }

        if (path.Equals("/api/window/drag/end", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
        {
            NativeWindowDrag.End();
            await SendStatusAsync(context.Response, 204);
            return;
        }

        if (path.Equals("/api/activity", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "POST")
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.InputStream);
            bool visible = document.RootElement.TryGetProperty("visible", out JsonElement value) && value.ValueKind == JsonValueKind.True;
            _audio.SetVisible(visible);
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

    private async Task HandleImportedVideoAsync(HttpListenerContext context, string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            await SendStatusAsync(context.Response, 404);
            return;
        }

        string id = Uri.UnescapeDataString(parts[1]);
        ImportedVideo? imported = _videoPreparation.FindImported(id);
        VideoPreparationSnapshot status = _videoPreparation.GetImportedStatus(id);
        if (imported is null || status.State != "ready" || !File.Exists(imported.OutputPath))
        {
            await SendStatusAsync(context.Response, 404);
            return;
        }

        if (parts[2].Equals("player", StringComparison.OrdinalIgnoreCase))
        {
            await SendVideoPlayerAsync(context.Response, $"/import/{Uri.EscapeDataString(id)}/video-file");
            return;
        }
        if (parts[2].Equals("video-file", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "video/webm";
            context.Response.Headers["Cache-Control"] = "public, max-age=3600";
            await SendFileAsync(context, imported.OutputPath);
            return;
        }

        await SendStatusAsync(context.Response, 404);
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
            string? preparedPath = _videoPreparation.GetPreparedProjectPath(project);
            if (preparedPath is null)
            {
                await SendStatusAsync(context.Response, 409);
                return;
            }
            await SendVideoPlayerAsync(
                context.Response,
                $"/wallpaper/{Uri.EscapeDataString(project.Id)}/video-file");
            return;
        }

        if (action.Equals("video-file", StringComparison.OrdinalIgnoreCase) && project.Type == "video")
        {
            string? videoPath = _videoPreparation.GetPreparedProjectPath(project);
            if (videoPath is null || !File.Exists(videoPath))
            {
                await SendStatusAsync(context.Response, 404);
                return;
            }

            context.Response.ContentType = "video/webm";
            context.Response.Headers["Cache-Control"] = "public, max-age=3600";
            await SendFileAsync(context, videoPath);
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
            bool valid = false;
            if (bounds.Length == 2 && long.TryParse(bounds[0], out long requestedStart))
            {
                start = Math.Clamp(requestedStart, 0, file.Length - 1);
                if (long.TryParse(bounds[1], out long requestedEnd))
                {
                    end = Math.Clamp(requestedEnd, start, file.Length - 1);
                }
                valid = requestedStart < file.Length;
            }
            else if (bounds.Length == 2 && long.TryParse(bounds[1], out long suffixLength) && suffixLength > 0)
            {
                suffixLength = Math.Min(suffixLength, file.Length);
                start = file.Length - suffixLength;
                end = file.Length - 1;
                valid = true;
            }

            if (!valid)
            {
                context.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                context.Response.Headers["Content-Range"] = $"bytes */{file.Length}";
                context.Response.Close();
                return;
            }
            context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
            context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{file.Length}";
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

        string escapedPath = string.Join('/', project.EntryFile
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return $"http://127.0.0.1:{Port}/wallpaper/{Uri.EscapeDataString(project.Id)}/files/{escapedPath}";
    }

    private static async Task SendVideoPlayerAsync(HttpListenerResponse response, string source)
    {
        string sourceJson = JsonSerializer.Serialize(source);
        string html = $$$"""
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
  let active=true,ready=false,timeout=0;
  const mute=()=>{video.muted=true;video.defaultMuted=true;video.volume=0};
  const reportError=message=>parent.postMessage({type:'spotifly:wallpaper-error',message},'*');
  const armTimeout=()=>{clearTimeout(timeout);if(active&&!ready)timeout=setTimeout(()=>reportError('VIDEO не загрузилось за 12 секунд'),12000)};
  const sync=()=>{mute();if(active&&!document.hidden){armTimeout();video.play().catch(error=>reportError('VIDEO: '+(error&&error.message||'воспроизведение заблокировано')))}else{clearTimeout(timeout);video.pause()}};
  if(!video.canPlayType('video/webm; codecs="vp9"'))reportError('Встроенный Chromium не поддерживает VP9');
  video.src={{{sourceJson}}};
  video.load();
  video.addEventListener('volumechange',mute);
  video.addEventListener('canplay',()=>{ready=true;clearTimeout(timeout);sync();parent.postMessage({type:'spotifly:wallpaper-ready'},'*')},{once:true});
  video.addEventListener('error',()=>{const code=video.error&&video.error.code||0;reportError('VIDEO не воспроизводится (ошибка '+code+')')});
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
