using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spotifly.WallpaperHost;

internal sealed record VideoPreparationSnapshot(
    string State,
    double Progress,
    long ProcessedMilliseconds,
    long DurationMilliseconds,
    string? Error);

internal sealed record ImportedVideo(string Id, string Title, string SourcePath, string OutputPath);

internal sealed class VideoPreparationService : IAsyncDisposable
{
    private sealed class PreparationJob
    {
        public required string Id { get; init; }
        public required string SourcePath { get; init; }
        public required string OutputPath { get; init; }
        public bool DeleteSourceOnSuccess { get; init; }
        public object Sync { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public string State { get; set; } = "queued";
        public double Progress { get; set; }
        public long ProcessedMilliseconds { get; set; }
        public long DurationMilliseconds { get; set; }
        public string? Error { get; set; }
        public Process? Process { get; set; }
    }

    private sealed record ImportedVideoMetadata(string Id, string Title, string SourcePath, string OutputPath);

    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(?<hours>\d+):(?<minutes>\d+):(?<seconds>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _cacheRoot;
    private readonly string _projectCache;
    private readonly string _importCache;
    private readonly string? _ffmpegPath;
    private readonly SemaphoreSlim _conversionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PreparationJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImportedVideoMetadata> _imports = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();

    public VideoPreparationService()
    {
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spotifly",
            "WallpaperCache");
        _projectCache = Path.Combine(_cacheRoot, "projects");
        _importCache = Path.Combine(_cacheRoot, "imports");
        Directory.CreateDirectory(_projectCache);
        Directory.CreateDirectory(_importCache);
        _ffmpegPath = FindFfmpeg();
        LoadImportedVideos();
    }

    public bool IsAvailable => _ffmpegPath is not null;

    public VideoPreparationSnapshot GetProjectStatus(WallpaperProject project)
    {
        string? source = WallpaperCatalog.SafeCombine(project.RootPath, project.EntryFile);
        string output = GetProjectOutputPath(project.Id);
        if (source is not null && IsReady(source, output))
        {
            return ReadySnapshot(output);
        }

        return GetJobSnapshot(project.Id, _ffmpegPath is null ? "converter-unavailable" : "idle");
    }

    public string? GetPreparedProjectPath(WallpaperProject project)
    {
        string? source = WallpaperCatalog.SafeCombine(project.RootPath, project.EntryFile);
        string output = GetProjectOutputPath(project.Id);
        return source is not null && IsReady(source, output) ? output : null;
    }

    public bool StartProject(WallpaperProject project)
    {
        if (_ffmpegPath is null || project.Type != "video") return false;
        string? source = WallpaperCatalog.SafeCombine(project.RootPath, project.EntryFile);
        if (source is null || !File.Exists(source)) return false;
        string output = GetProjectOutputPath(project.Id);
        if (IsReady(source, output)) return true;
        return StartJob(project.Id, source, output, deleteSourceOnSuccess: false);
    }

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out PreparationJob? job)) return false;
        lock (job.Sync)
        {
            if (job.State is "ready" or "error" or "cancelled") return false;
            job.State = "cancelled";
            job.Error = null;
            job.Cancellation.Cancel();
            try
            {
                if (job.Process is { HasExited: false }) job.Process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
        return true;
    }

    public async Task<ImportedVideo?> ImportAsync(Stream source, string? fileName, CancellationToken cancellationToken)
    {
        if (_ffmpegPath is null) return null;
        string extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (extension is not ".mp4" and not ".mov" and not ".m4v" and not ".webm") extension = ".mp4";
        string id = "local-" + Guid.NewGuid().ToString("N");
        string title = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(title)) title = "Локальное видео";
        string sourcePath = Path.Combine(_importCache, id + extension);
        string outputPath = Path.Combine(_importCache, id + ".webm");

        await using (FileStream output = new(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            await source.CopyToAsync(output, 128 * 1024, cancellationToken);
        }

        var metadata = new ImportedVideoMetadata(id, title, sourcePath, outputPath);
        _imports[id] = metadata;
        string metadataPath = Path.Combine(_importCache, id + ".json");
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata), cancellationToken);
        if (!StartJob(id, sourcePath, outputPath, deleteSourceOnSuccess: true)) return null;
        return new ImportedVideo(id, title, sourcePath, outputPath);
    }

    public ImportedVideo? FindImported(string id)
    {
        return _imports.TryGetValue(id, out ImportedVideoMetadata? metadata)
            ? new ImportedVideo(metadata.Id, metadata.Title, metadata.SourcePath, metadata.OutputPath)
            : null;
    }

    public VideoPreparationSnapshot GetImportedStatus(string id)
    {
        if (!_imports.TryGetValue(id, out ImportedVideoMetadata? metadata))
        {
            return new VideoPreparationSnapshot("missing", 0, 0, 0, "Видео не найдено");
        }
        if (File.Exists(metadata.OutputPath) && new FileInfo(metadata.OutputPath).Length > 0)
        {
            return ReadySnapshot(metadata.OutputPath);
        }
        return GetJobSnapshot(id, _ffmpegPath is null ? "converter-unavailable" : "idle");
    }

    private bool StartJob(string id, string sourcePath, string outputPath, bool deleteSourceOnSuccess)
    {
        if (_ffmpegPath is null) return false;
        if (_jobs.TryGetValue(id, out PreparationJob? existing))
        {
            lock (existing.Sync)
            {
                if (existing.State is "queued" or "preparing") return true;
            }
            _jobs.TryRemove(id, out _);
        }

        var job = new PreparationJob
        {
            Id = id,
            SourcePath = sourcePath,
            OutputPath = outputPath,
            DeleteSourceOnSuccess = deleteSourceOnSuccess
        };
        if (!_jobs.TryAdd(id, job)) return false;
        _ = Task.Run(() => RunJobAsync(job, _shutdown.Token));
        return true;
    }

    private async Task RunJobAsync(PreparationJob job, CancellationToken shutdownToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, shutdownToken);
        CancellationToken cancellationToken = linked.Token;
        string temporaryPath = job.OutputPath + ".part";
        bool gateEntered = false;
        try
        {
            await _conversionGate.WaitAsync(cancellationToken);
            gateEntered = true;
            lock (job.Sync) job.State = "preparing";
            Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
            TryDelete(temporaryPath);
            long duration = await ProbeDurationMillisecondsAsync(job.SourcePath, cancellationToken);
            lock (job.Sync) job.DurationMilliseconds = duration;

            var start = new ProcessStartInfo
            {
                FileName = _ffmpegPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[]
            {
                "-hide_banner", "-loglevel", "warning", "-y", "-i", job.SourcePath,
                "-map", "0:v:0", "-an",
                "-vf", "scale=w='min(1920,iw)':h=-2:flags=lanczos,fps=30",
                "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8",
                "-row-mt", "1", "-threads", "0", "-crf", "32", "-b:v", "0",
                "-pix_fmt", "yuv420p", "-progress", "pipe:1", "-nostats", "-f", "webm", temporaryPath
            })
            {
                start.ArgumentList.Add(argument);
            }

            using Process process = new() { StartInfo = start };
            var errors = new StringBuilder();
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null) return;
                lock (errors)
                {
                    if (errors.Length < 4096) errors.AppendLine(eventArgs.Data);
                }
            };
            if (!process.Start()) throw new InvalidOperationException("Не удалось запустить видеоконвертер");
            lock (job.Sync) job.Process = process;
            process.BeginErrorReadLine();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
                if (!long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds)) continue;
                long milliseconds = Math.Max(0, microseconds / 1000);
                lock (job.Sync)
                {
                    job.ProcessedMilliseconds = milliseconds;
                    job.Progress = job.DurationMilliseconds > 0
                        ? Math.Clamp(milliseconds * 100d / job.DurationMilliseconds, 0, 99.5)
                        : 0;
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                string error;
                lock (errors) error = errors.ToString().Trim();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFmpeg завершился с ошибкой" : LastLine(error));
            }
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            {
                throw new InvalidOperationException("Конвертер не создал видеофайл");
            }

            File.Move(temporaryPath, job.OutputPath, overwrite: true);
            if (job.DeleteSourceOnSuccess) TryDelete(job.SourcePath);
            lock (job.Sync)
            {
                job.State = "ready";
                job.Progress = 100;
                job.ProcessedMilliseconds = job.DurationMilliseconds;
                job.Process = null;
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            lock (job.Sync)
            {
                job.State = "cancelled";
                job.Process = null;
            }
        }
        catch (Exception exception)
        {
            TryDelete(temporaryPath);
            lock (job.Sync)
            {
                job.State = "error";
                job.Error = exception.Message;
                job.Process = null;
            }
        }
        finally
        {
            if (gateEntered) _conversionGate.Release();
        }
    }

    private async Task<long> ProbeDurationMillisecondsAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (_ffmpegPath is null) return 0;
        var start = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(sourcePath);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось проверить видео");
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Match match = DurationPattern.Match(error);
        if (!match.Success) return 0;
        double hours = double.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture);
        double minutes = double.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);
        double seconds = double.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture);
        return (long)TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds).TotalMilliseconds;
    }

    private VideoPreparationSnapshot GetJobSnapshot(string id, string fallbackState)
    {
        if (!_jobs.TryGetValue(id, out PreparationJob? job))
        {
            return new VideoPreparationSnapshot(fallbackState, 0, 0, 0, null);
        }
        lock (job.Sync)
        {
            return new VideoPreparationSnapshot(
                job.State,
                Math.Round(job.Progress, 1),
                job.ProcessedMilliseconds,
                job.DurationMilliseconds,
                job.Error);
        }
    }

    private string GetProjectOutputPath(string id) => Path.Combine(_projectCache, SafeId(id) + ".webm");

    private static VideoPreparationSnapshot ReadySnapshot(string outputPath) =>
        new("ready", 100, new FileInfo(outputPath).Length, new FileInfo(outputPath).Length, null);

    private static bool IsReady(string sourcePath, string outputPath)
    {
        if (!File.Exists(sourcePath) || !File.Exists(outputPath)) return false;
        var source = new FileInfo(sourcePath);
        var output = new FileInfo(outputPath);
        return output.Length > 0 && output.LastWriteTimeUtc >= source.LastWriteTimeUtc;
    }

    private void LoadImportedVideos()
    {
        foreach (string metadataPath in Directory.EnumerateFiles(_importCache, "local-*.json"))
        {
            try
            {
                ImportedVideoMetadata? metadata = JsonSerializer.Deserialize<ImportedVideoMetadata>(File.ReadAllText(metadataPath));
                if (metadata is not null && metadata.Id.StartsWith("local-", StringComparison.Ordinal))
                {
                    _imports[metadata.Id] = metadata;
                }
            }
            catch
            {
            }
        }
    }

    private static string? FindFfmpeg()
    {
        foreach (string candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
            }
        }
        return null;
    }

    private static string SafeId(string id) =>
        new(id.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());

    private static string LastLine(string value)
    {
        string[] lines = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string line = lines.LastOrDefault() ?? value;
        return line.Length <= 240 ? line : line[^240..];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (PreparationJob job in _jobs.Values)
        {
            Cancel(job.Id);
        }
        await _conversionGate.WaitAsync();
        _conversionGate.Release();
        _conversionGate.Dispose();
        _shutdown.Dispose();
    }
}
