using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Baton.Cli.Daemon;
using Baton.Tests.Shared;
using Baton.Vendors;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1946 — the daemon's glass listener, driven over a real loopback socket rather than a faked
/// context. The claim under test is "an HTTP client gets these bytes from these routes", and a test
/// double for <see cref="HttpListener"/> could not falsify it: the routes, the SSE framing, and the
/// bind itself are all behaviours of the listener, not of code around it.
/// </summary>
public sealed class GlassHttpServiceTests : IDisposable
{
    private readonly string _tempHome;

    public GlassHttpServiceTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-glass-listener-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    /// <summary><see cref="HttpListener"/> cannot bind port 0, so a free port is reserved and released
    /// first. A race with another process between release and bind is possible in principle and has no
    /// cheaper alternative; it would surface as a bind failure, never as a wrong assertion.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed record Harness(GlassHttpService Service, string BaseUrl, string ProjectionPath, StringWriter Log);

    private async Task<Harness> StartAsync(CancellationToken cancellationToken, bool listen = true, string? projection = null)
    {
        var projectionPath = Path.Combine(_tempHome, "projection.json");
        if (projection is not null)
        {
            await File.WriteAllTextAsync(projectionPath, projection, cancellationToken);
        }

        var port = FreePort();
        var log = new StringWriter();
        var service = new GlassHttpService(
            new DaemonSettings { Glass = new GlassListenerSettings { Listen = listen, Port = port } },
            projectionPath,
            TimeSpan.FromMilliseconds(25),
            log);

        await service.StartAsync(cancellationToken);

        // StartAsync returns as soon as ExecuteAsync yields; the first yield is inside the accept
        // loop, i.e. after every bind. Poll rather than sleep a fixed span so a slow machine does not
        // flake and a fast one does not wait.
        for (var attempt = 0; attempt < 200 && service.BoundPrefixes.Count == 0 && listen; attempt++)
        {
            // A poll interval, not a ceiling: the 200 attempts around it are the ceiling.
            // wait-ok: the ceiling is the attempt count above, plus the caller's CancellationToken.
            await Task.Delay(25, cancellationToken);
        }

        return new Harness(service, $"http://127.0.0.1:{port}", projectionPath, log);
    }

    [Fact]
    public async Task Binds_loopback_and_logs_the_bound_urls_once()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var harness = await StartAsync(cts.Token);
        try
        {
            Assert.Contains(harness.Service.BoundPrefixes, p => p.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            // Uri.Host keeps the brackets on an IPv6 literal, which IPAddress.Parse rejects.
            // IsPermittedAddress is the address half of the rule -- all a bound prefix can carry, the
            // interface having already been consumed at selection time.
            Assert.All(
                harness.Service.BoundPrefixes,
                prefix => Assert.True(
                    GlassBindPolicy.IsPermittedAddress(IPAddress.Parse(new Uri(prefix).Host.Trim('[', ']')))));

            var log = harness.Log.ToString();
            Assert.Contains("serving the fleet glass at", log, StringComparison.Ordinal);
            Assert.Equal(1, log.Split("serving the fleet glass at").Length - 1);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>#2130, spec/baton.md §7 — the diagnostic this exists for reads the heartbeat, not
    /// <see cref="GlassHttpService.BoundPrefixes"/> directly, so the ledger write is what has to be
    /// pinned rather than the property alone.</summary>
    [Fact]
    public async Task Reports_the_bound_prefixes_onto_the_shared_tick_ledger_for_the_heartbeat()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var harness = await StartAsync(cts.Token);
        try
        {
            var heartbeat = DaemonTickLedger.Instance.RenderHeartbeatJson(HostLoadSample.Capture(DateTimeOffset.UtcNow));
            using var doc = System.Text.Json.JsonDocument.Parse(heartbeat);
            var reported = doc.RootElement.GetProperty("glassBoundPrefixes")
                .EnumerateArray().Select(e => e.GetString()!).ToList();

            Assert.Equal(harness.Service.BoundPrefixes, reported);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Serves_nothing_at_all_when_the_operator_has_not_opted_in()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var harness = await StartAsync(cts.Token, listen: false, projection: """{"rooms":[]}""");
        try
        {
            Assert.Empty(harness.Service.BoundPrefixes);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => client.GetAsync($"{harness.BaseUrl}/projection.json", cts.Token));
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Serves_the_repos_own_glass_page_at_the_root_with_the_daemon_marker_injected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var harness = await StartAsync(cts.Token);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var response = await client.GetAsync($"{harness.BaseUrl}/", cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

            // The SAME page the artifact is published from -- asserted against the repo's own copy,
            // not against a substring that a second, drifting copy under src/ would also satisfy.
            var repoPage = await File.ReadAllTextAsync(RepoGlassHtmlPath(), cts.Token);
            Assert.Equal(GlassPage.Inject(repoPage).ReplaceLineEndings(), body.ReplaceLineEndings());

            // #2053 -- over the MARKUP, not the bytes: `Assert.Contains(SourceMetaTag, body)` passed
            // on the shipped bug, because the header comment quotes the tag verbatim. This is the
            // assertion closest to the issue's own `curl /` measurement, so it is the one that had to
            // start discriminating; GlassMarkup owns the reason.
            Assert.Single(
                Regex.Matches(GlassMarkup.Of(body), Regex.Escape(GlassPage.SourceMetaTag)));
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Serves_the_same_origin_standalone_manifest_and_its_raster_icons()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var harness = await StartAsync(cts.Token);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var page = await client.GetStringAsync($"{harness.BaseUrl}/", cts.Token);
            Assert.Contains(
                $"<link rel=\"manifest\" href=\"{GlassWebAppAssets.ManifestPath}\">", page,
                StringComparison.Ordinal);

            var manifestResponse = await client.GetAsync(
                $"{harness.BaseUrl}{GlassWebAppAssets.ManifestPath}", cts.Token);
            Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
            Assert.Equal("application/manifest+json", manifestResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", manifestResponse.Headers.CacheControl?.ToString());

            using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync(cts.Token));
            var root = manifest.RootElement;
            Assert.Equal("/", root.GetProperty("id").GetString());
            Assert.Equal("/", root.GetProperty("start_url").GetString());
            Assert.Equal("/", root.GetProperty("scope").GetString());
            Assert.Equal("standalone", root.GetProperty("display").GetString());

            var icons = root.GetProperty("icons").EnumerateArray().ToArray();
            Assert.Collection(
                icons,
                icon => AssertIcon(icon, GlassWebAppAssets.Icon192Path, "192x192", 192),
                icon => AssertIcon(icon, GlassWebAppAssets.Icon512Path, "512x512", 512));

            foreach (var icon in icons)
            {
                var response = await client.GetAsync(
                    $"{harness.BaseUrl}{icon.GetProperty("src").GetString()}", cts.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                AssertPngIsExpectedIcon(
                    await response.Content.ReadAsByteArrayAsync(cts.Token),
                    int.Parse(icon.GetProperty("sizes").GetString()![..3],
                        System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Serves_the_private_navigation_only_service_worker_without_a_cacheable_live_route()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var harness = await StartAsync(cts.Token, projection: """{"rooms":[]}""");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var worker = await client.GetAsync($"{harness.BaseUrl}{GlassWebAppAssets.ServiceWorkerPath}", cts.Token);
            var source = await worker.Content.ReadAsStringAsync(cts.Token);

            Assert.Equal(HttpStatusCode.OK, worker.StatusCode);
            Assert.Equal("application/javascript", worker.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", worker.Headers.CacheControl?.ToString());
            Assert.Contains("isDashboardNavigation(event.request)", source, StringComparison.Ordinal);
            Assert.Contains("url.origin === self.location.origin", source, StringComparison.Ordinal);
            Assert.Contains("DASHBOARD_PATHS.has(url.pathname)", source, StringComparison.Ordinal);
            Assert.Contains("cache: \"no-store\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("caches.", source, StringComparison.Ordinal);

            var projection = await client.GetAsync($"{harness.BaseUrl}/projection.json", cts.Token);
            Assert.Equal("no-store", projection.Headers.CacheControl?.ToString());
            Assert.Equal("application/json", projection.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Serves_the_projection_file_byte_for_byte_and_404s_before_the_first_write()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var harness = await StartAsync(cts.Token);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            var missing = await client.GetAsync($"{harness.BaseUrl}/projection.json", cts.Token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            const string content = """{"derived_at":"2026-09-07T00:00:00.0000000+00:00","rooms":[{"path":"r"}]}""";
            await File.WriteAllTextAsync(harness.ProjectionPath, content, cts.Token);

            var response = await client.GetAsync($"{harness.BaseUrl}/projection.json", cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(content, await response.Content.ReadAsStringAsync(cts.Token));

            // The route releases its handle before responding, so this only proves the write is not
            // blocked afterwards. What the share mode itself buys is
            // The_projection_route_opens_the_file_so_a_rename_over_it_can_still_succeed, which holds a
            // handle across the replace.
            FleetProjectionWriter.WriteAtomic(harness.ProjectionPath, """{"rooms":[]}""");
            Assert.Equal("""{"rooms":[]}""", await File.ReadAllTextAsync(harness.ProjectionPath, cts.Token));
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// #2028 review — the share mode the projection route opens with, pinned by the permission it
    /// actually grants rather than by reading the flag back. The route releases its handle before
    /// responding, so the difference is only observable by holding one ACROSS a replace; and
    /// <see cref="FleetProjectionWriter.WriteAtomic"/> never throws (it logs and skips), so "does not
    /// throw" would pass in either state and falsify nothing.
    /// <para>
    /// Measured while writing this test, and it is why the arms use <see cref="File.Replace(string, string, string)"/>
    /// rather than <c>WriteAtomic</c>: on Windows <c>File.Move(overwrite: true)</c> refuses a target
    /// with any open handle whatever its share mode, so a <c>WriteAtomic</c> arm would be red for BOTH
    /// values of the flag and discriminate nothing. <c>ReplaceFile</c> is the rename-over that honours
    /// delete-sharing, which is the permission <see cref="GlassHttpService.ProjectionShare"/> exists to
    /// grant. Windows-only: POSIX renames ignore share modes entirely, and Windows is the platform CI
    /// runs (#1405).
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_projection_route_opens_the_file_so_a_rename_over_it_can_still_succeed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var path = Path.Combine(_tempHome, "share-mode.json");
        var replacement = Path.Combine(_tempHome, "share-mode.new");
        const string before = """{"rooms":[{"path":"before"}]}""";
        const string after = """{"rooms":[{"path":"after"}]}""";

        // POSITIVE ARM: the production constant itself, so dropping FileShare.Delete from the route
        // fails here rather than only in a re-typed copy of it.
        await File.WriteAllTextAsync(path, before, cts.Token);
        await File.WriteAllTextAsync(replacement, after, cts.Token);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, GlassHttpService.ProjectionShare))
        {
            File.Replace(replacement, path, destinationBackupFileName: null);
        }

        Assert.Equal(after, await File.ReadAllTextAsync(path, cts.Token));

        // CONTROL ARM: the same reader one enum flag short. Without delete-sharing the rename-over is
        // refused outright and the writer's new content is lost -- which is what the route would cost
        // the writer if it opened FileShare.ReadWrite alone.
        await File.WriteAllTextAsync(path, before, cts.Token);
        await File.WriteAllTextAsync(replacement, after, cts.Token);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(
                () => File.Replace(replacement, path, destinationBackupFileName: null));
        }

        Assert.Equal(before, await File.ReadAllTextAsync(path, cts.Token));
    }

    [Fact]
    public async Task Refuses_every_method_but_GET_and_404s_an_unknown_route()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var harness = await StartAsync(cts.Token, projection: """{"rooms":[]}""");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
            {
                var response = await client.SendAsync(
                    new HttpRequestMessage(method, $"{harness.BaseUrl}/projection.json"), cts.Token);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            }

            // The drill-down payloads C-11 assigns to slice 2 are not served by this slice, and the
            // route table is what says so.
            foreach (var path in new[] { "/rooms", "/stdout", "/timeline", "/artifacts" })
            {
                var response = await client.GetAsync($"{harness.BaseUrl}{path}", cts.Token);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Emits_an_event_when_the_projection_changes_and_not_otherwise()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var harness = await StartAsync(cts.Token, projection: """{"rooms":[]}""");
        try
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(
                $"{harness.BaseUrl}/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);

            // Read on a background pump rather than cancelling individual reads: cancelling a read on
            // an HttpClient response stream disposes the connection, so the control arm below would
            // destroy the very stream the positive arm then needs.
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var pump = Task.Run(
                async () =>
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    while (!pumpStop.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(pumpStop.Token);
                        if (line is null)
                        {
                            return;
                        }

                        lines.Enqueue(line);
                    }
                },
                pumpStop.Token);

            await WaitForAsync(() => lines.Contains(": connected"), TimeSpan.FromSeconds(10));

            // CONTROL ARM, read first: with the file untouched, no `event:` frame arrives inside a
            // window dozens of poll intervals wide. Without it, the assertion below would pass just as
            // happily against a stream that emits continuously, which is the failure that would make
            // this whole test meaningless.
            // The arm asserts nothing arrives across dozens of 25ms poll intervals, so it must elapse.
            // wait-ok: this delay IS the measurement, not a ceiling on one.
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            Assert.DoesNotContain(lines, line => line.StartsWith("event:", StringComparison.Ordinal));

            FleetProjectionWriter.WriteAtomic(harness.ProjectionPath, """{"rooms":[{"path":"r"}]}""");

            await WaitForAsync(
                () => lines.Contains("event: projection"), TimeSpan.FromSeconds(15));
            Assert.Contains(lines, line => Regex.IsMatch(line, @"^id: \d+$"));
            Assert.Contains(lines, line => line.StartsWith("data: {\"version\":", StringComparison.Ordinal));

            await pumpStop.CancelAsync();
            await pump.ContinueWith(_ => { }, TaskScheduler.Default);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, failing with a legible message
    /// rather than a timeout exception if it never does.</summary>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            // wait-ok: a poll interval; `budget` is the ceiling and every caller passes seconds of it.
            await Task.Delay(25);
        }

        Assert.Fail($"The expected stream content did not arrive within {budget}.");
    }

    private static void AssertIcon(JsonElement icon, string path, string size, int dimension)
    {
        Assert.Equal(path, icon.GetProperty("src").GetString());
        Assert.Equal(size, icon.GetProperty("sizes").GetString());
        Assert.Equal("image/png", icon.GetProperty("type").GetString());
        Assert.Equal(dimension, int.Parse(size[..3], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AssertPngIsExpectedIcon(byte[] png, int dimension)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.True(png.AsSpan().StartsWith(signature), "PNG signature is invalid.");

        var position = signature.Length;
        var idat = new MemoryStream();
        var sawHeader = false;
        var sawEnd = false;
        while (position < png.Length)
        {
            Assert.True(png.Length - position >= 12, "PNG chunk is truncated.");
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position, 4));
            Assert.True(length <= int.MaxValue && length <= png.Length - position - 12, "PNG chunk length is invalid.");
            var dataLength = (int)length;
            var type = png.AsSpan(position + 4, 4);
            var data = png.AsSpan(position + 8, dataLength);
            var expectedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                png.AsSpan(position + 8 + dataLength, 4));
            Assert.Equal(expectedCrc, PngCrc32(type, data));

            if (type.SequenceEqual("IHDR"u8))
            {
                Assert.False(sawHeader, "PNG has more than one IHDR chunk.");
                Assert.Equal(signature.Length, position);
                Assert.Equal(13, dataLength);
                Assert.Equal(dimension, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data));
                Assert.Equal(dimension, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[4..]));
                Assert.Equal((byte)8, data[8]);
                Assert.Equal((byte)6, data[9]);
                Assert.Equal((byte)0, data[10]);
                Assert.Equal((byte)0, data[11]);
                Assert.Equal((byte)0, data[12]);
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                Assert.True(sawHeader && !sawEnd, "PNG IDAT chunk is out of order.");
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                Assert.Equal(0, dataLength);
                Assert.True(sawHeader, "PNG IEND chunk appears before IHDR.");
                sawEnd = true;
                Assert.Equal(png.Length, position + 12);
            }

            position += dataLength + 12;
        }

        Assert.True(sawHeader && sawEnd && idat.Length > 0, "PNG must contain IHDR, IDAT, and IEND chunks.");
        byte[] scanlines;
        using (var compressed = new MemoryStream(idat.ToArray()))
        using (var decompressor = new ZLibStream(compressed, CompressionMode.Decompress))
        using (var decoded = new MemoryStream())
        {
            decompressor.CopyTo(decoded);
            scanlines = decoded.ToArray();
        }

        var stride = (dimension * 4) + 1;
        Assert.Equal(dimension * stride, scanlines.Length);
        var edge = dimension * 3 / 16;
        var paneStart = dimension * 5 / 16;
        var paneEnd = dimension * 11 / 16;
        for (var y = 0; y < dimension; y++)
        {
            var row = y * stride;
            Assert.Equal((byte)0, scanlines[row]);
            for (var x = 0; x < dimension; x++)
            {
                var offset = row + 1 + (x * 4);
                var inGlass = x >= edge && x < dimension - edge && y >= edge && y < dimension - edge;
                var inPane = x >= paneStart && x < paneEnd && y >= paneStart && y < paneEnd;
                Assert.Equal(inPane ? (byte)250 : inGlass ? (byte)96 : (byte)35, scanlines[offset]);
                Assert.Equal(inPane ? (byte)250 : inGlass ? (byte)213 : (byte)40, scanlines[offset + 1]);
                Assert.Equal(inPane ? (byte)248 : inGlass ? (byte)208 : (byte)48, scanlines[offset + 2]);
                Assert.Equal(byte.MaxValue, scanlines[offset + 3]);
            }
        }
    }

    private static uint PngCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type)
        {
            crc = PngCrc32Step(crc, value);
        }

        foreach (var value in data)
        {
            crc = PngCrc32Step(crc, value);
        }

        return ~crc;
    }

    private static uint PngCrc32Step(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xEDB88320U);
        }

        return crc;
    }

    private static string RepoGlassHtmlPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Baton.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tools", "fleet-glass", "glass.html");
    }
}
