using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace LiveSub.Services;

public sealed record ModelDownloadProgress(long Received, long Total, double BytesPerSecond);

public static class ModelDownloadService
{
    private const int BufferSize = 128 * 1024;
    private const long ParallelThreshold = 16L * 1024 * 1024;
    private const int SegmentAttempts = 3;
    private const int ParallelSegments = 4;
    private const string ManifestSuffix = ".manifest";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static async Task DownloadAsync(
        ModelDescriptor model,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(model.FullPath)!);
        if (File.Exists(model.FullPath))
        {
            var length = new FileInfo(model.FullPath).Length;
            if (length >= model.MinBytes)
            {
                TryDelete(model.FullPath + ".part" + ManifestSuffix);
                return;
            }
            TryDelete(model.FullPath);
        }

        var attempts = new List<string>();
        if (model.Urls.Length > 0)
        {
            attempts.Add(model.Urls[0]);
            attempts.Add(model.Urls[0]);
        }
        for (var i = 1; i < model.Urls.Length; i++) attempts.Add(model.Urls[i]);

        Exception? lastError = null;
        for (var attempt = 0; attempt < attempts.Count; attempt++)
        {
            var url = attempts[attempt];
            try
            {
                AppPaths.Log($"Downloading {model.DisplayName} from {url} (attempt {attempt + 1})");
                await DownloadFromSourceAsync(model, url, progress, ct);
                try
                {
                    VerifyDownload(model);
                }
                catch
                {
                    TryDelete(model.FullPath);
                    throw;
                }
                AppPaths.Log($"Model downloaded: {model.FullPath} ({new FileInfo(model.FullPath).Length:N0} bytes)");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                AppPaths.Log($"Download source failed ({url}): {ex.Message}");
                if (attempt + 1 < attempts.Count) await Task.Delay(1500, ct);
            }
        }

        throw new IOException(
            "模型下载失败，请检查网络后重试。" + (lastError == null ? "" : " " + lastError.Message),
            lastError);
    }

    public static void Delete(ModelDescriptor model)
    {
        TryDelete(model.FullPath);
        TryDelete(model.FullPath + ".part");
        TryDelete(model.FullPath + ".part" + ManifestSuffix);
        for (var i = 0; i < 16; i++) TryDelete($"{model.FullPath}.part.{i}");
        AppPaths.Log($"Model deleted: {model.FullPath}");
    }

    private static async Task DownloadFromSourceAsync(
        ModelDescriptor model,
        string url,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var target = model.FullPath;
        var part = target + ".part";
        var probe = await ProbeAsync(url, ct);

        if (probe.SupportsRanges && probe.Length > ParallelThreshold)
        {
            EnsureResumeIdentity(part, url, probe.Length, ParallelSegments);
            await DownloadParallelAsync(url, part, probe.Length, progress, ct);
        }
        else
        {
            EnsureResumeIdentity(part, url, probe.Length, 0);
            await DownloadSingleAsync(url, part, probe.SupportsRanges, probe.Length, progress, ct);
        }

        var finalLength = new FileInfo(part).Length;
        if (probe.Length > 0 && finalLength != probe.Length)
            throw new IOException($"下载不完整：{finalLength:N0} / {probe.Length:N0} 字节");

        File.Move(part, target, true);
        TryDelete(part + ManifestSuffix);
    }

    private static void EnsureResumeIdentity(string part, string url, long length, int segments)
    {
        var manifest = part + ManifestSuffix;
        var expected = $"{url}\n{length}\n{segments}";
        try
        {
            if (File.Exists(manifest) && File.ReadAllText(manifest) == expected) return;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Read download manifest failed: " + ex.Message);
        }

        TryDelete(part);
        for (var i = 0; i < 16; i++) TryDelete($"{part}.{i}");
        try
        {
            File.WriteAllText(manifest, expected);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Write download manifest failed: " + ex.Message);
        }
    }

    private static async Task<(bool SupportsRanges, long Length)> ProbeAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct, 40);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var length = response.Content.Headers.ContentRange?.Length ?? -1;
            return (true, length);
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var length = response.Content.Headers.ContentLength ?? -1;
            return (false, length);
        }

        response.EnsureSuccessStatusCode();
        return (false, -1);
    }

    private static async Task DownloadParallelAsync(
        string url,
        string part,
        long length,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var segments = ParallelSegments;
        var segmentLength = (length + segments - 1) / segments;
        var plans = new List<(long Start, long End, string File)>(segments);
        long existing = 0;

        TryDelete(part);
        for (var i = segments; i < 16; i++) TryDelete($"{part}.{i}");

        for (var i = 0; i < segments; i++)
        {
            var start = i * segmentLength;
            var end = Math.Min(length - 1, start + segmentLength - 1);
            if (start > end) continue;
            var file = $"{part}.{i}";
            var expected = end - start + 1;
            var have = File.Exists(file) ? new FileInfo(file).Length : 0;
            if (have > expected)
            {
                TryDelete(file);
                have = 0;
            }
            existing += have;
            plans.Add((start, end, file));
        }

        var reporter = new ProgressReporter(progress, existing, length);
        reporter.PublishInitial();

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = plans.Select(plan => RunSegmentAsync(plan.Start, plan.End, plan.File)).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            abort.Cancel();
            throw;
        }

        await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            foreach (var plan in plans)
            {
                await using (var input = new FileStream(plan.File, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, BufferSize, ct);
                }
                TryDelete(plan.File);
            }
            await output.FlushAsync(ct);
        }

        reporter.Complete(length);
        return;

        async Task RunSegmentAsync(long start, long end, string file)
        {
            try
            {
                await DownloadSegmentAsync(url, file, start, end, reporter, abort.Token);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                abort.Cancel();
                throw;
            }
        }
    }

    private static async Task DownloadSegmentAsync(
        string url,
        string file,
        long start,
        long end,
        ProgressReporter reporter,
        CancellationToken ct)
    {
        var expected = end - start + 1;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= SegmentAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var have = File.Exists(file) ? new FileInfo(file).Length : 0;
            if (have >= expected) return;
            if (have > expected)
            {
                TryDelete(file);
                have = 0;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start + have, end);
                using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct, 40);
                if (response.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException($"下载源返回 HTTP {(int)response.StatusCode}");

                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await ReadAsync(input, buffer, ct, 45);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    reporter.Add(read);
                }
                await output.FlushAsync(ct);

                if (new FileInfo(file).Length == expected) return;
                throw new IOException("分段下载长度不完整。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                AppPaths.Log($"Segment {start}-{end} attempt {attempt} failed: {ex.Message}");
                if (attempt < SegmentAttempts) await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }

        throw new IOException("分段下载失败。", lastError);
    }

    private static async Task DownloadSingleAsync(
        string url,
        string part,
        bool supportsRanges,
        long knownLength,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (knownLength > 0 && existing >= knownLength) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0 && supportsRanges)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct, 40);
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) existing = 0;
        response.EnsureSuccessStatusCode();

        var length = response.Content.Headers.ContentRange?.Length
                     ?? response.Content.Headers.ContentLength
                     ?? knownLength;
        var reporter = new ProgressReporter(progress, existing, length);
        reporter.PublishInitial();

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write,
                         FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                var read = await ReadAsync(input, buffer, ct, 45);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                reporter.Add(read);
            }
            await output.FlushAsync(ct);
        }

        var finalLength = new FileInfo(part).Length;
        if (length > 0 && finalLength != length)
            throw new IOException($"下载不完整：{finalLength:N0} / {length:N0} 字节");
        reporter.Complete(finalLength);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption option,
        CancellationToken ct,
        int timeoutSeconds)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await Client.SendAsync(request, option, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("连接模型服务器超时。");
        }
    }

    private static async Task<int> ReadAsync(Stream input, byte[] buffer, CancellationToken ct, int timeoutSeconds)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await input.ReadAsync(buffer, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("下载超时，网络可能不稳定。");
        }
    }

    private static void VerifyDownload(ModelDescriptor model)
    {
        var info = new FileInfo(model.FullPath);
        if (!info.Exists || info.Length < model.MinBytes)
            throw new IOException($"模型文件不完整（{info.Length:N0} 字节）。");

        var magic = new byte[4];
        using var stream = File.OpenRead(model.FullPath);
        var read = stream.Read(magic, 0, 4);
        if (read != 4) throw new IOException("模型文件无法读取。");

        var isGguf = magic[0] == 'G' && magic[1] == 'G' && magic[2] == 'U' && magic[3] == 'F';
        var isGgml = (magic[0] == 'l' && magic[1] == 'm' && magic[2] == 'g' && magic[3] == 'g')
                     || (magic[0] == 'g' && magic[1] == 'g' && magic[2] == 'm' && magic[3] == 'l');

        if (model.Kind == ModelKind.Translation && !isGguf)
            throw new IOException("下载内容不是有效的 GGUF 模型文件。");
        if (model.Kind == ModelKind.SpeechRecognition && !isGgml)
            throw new IOException("下载内容不是有效的 Whisper 模型文件。");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Delete file failed ({path}): {ex.Message}");
        }
    }

    private sealed class ProgressReporter
    {
        private readonly IProgress<ModelDownloadProgress>? _progress;
        private readonly object _lock = new();
        private readonly long _initial;
        private readonly long _total;
        private long _received;
        private long _lastSampleTicks;
        private long _lastSampleBytes;
        private long _lastReportTicks;
        private double _speed;

        public ProgressReporter(IProgress<ModelDownloadProgress>? progress, long initial, long total)
        {
            _progress = progress;
            _initial = initial;
            _total = total;
            _received = initial;
            _lastSampleTicks = Environment.TickCount64;
            _lastSampleBytes = initial;
        }

        public void PublishInitial() =>
            _progress?.Report(new ModelDownloadProgress(_initial, _total, 0));

        public void Add(int count)
        {
            long received;
            long now;
            lock (_lock)
            {
                _received += count;
                received = _received;
                now = Environment.TickCount64;
                var sampleElapsed = now - _lastSampleTicks;
                if (sampleElapsed >= 500)
                {
                    _speed = (received - _lastSampleBytes) * 1000.0 / sampleElapsed;
                    _lastSampleBytes = received;
                    _lastSampleTicks = now;
                }
                if (now - _lastReportTicks < 250) return;
                _lastReportTicks = now;
            }
            _progress?.Report(new ModelDownloadProgress(received, _total, _speed));
        }

        public void Complete(long size)
        {
            lock (_lock)
            {
                _received = size;
                _speed = 0;
            }
            _progress?.Report(new ModelDownloadProgress(size, _total > 0 ? _total : size, 0));
        }
    }
}
