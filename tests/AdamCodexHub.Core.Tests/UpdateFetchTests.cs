using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AdamCodexHub.Core.Maintenance;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// The download half, over a real socket. A local server rather than a mock: the point of these tests is
/// that the bytes cross a network boundary and are then checked, and a stubbed HttpClient would skip the
/// part that can actually go wrong.
/// </summary>
public sealed class UpdateFetchTests : IDisposable
{
    private sealed class TinyServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<string, byte[]> _routes = new();

        public TinyServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public void Add(string path, byte[] content) => _routes[path] = content;

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                        var requestLine = await reader.ReadLineAsync().ConfigureAwait(false) ?? string.Empty;
                        var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync().ConfigureAwait(false)))
                        {
                        }

                        var body = _routes.TryGetValue(path, out var found)
                            ? found
                            : Encoding.ASCII.GetBytes("not found");

                        var header = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n" +
                            "Content-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header).ConfigureAwait(false);
                        await stream.WriteAsync(body).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codexhub-fetch-" + Guid.NewGuid().ToString("N"));

    private readonly string _stagingRoot;
    private readonly TinyServer _server = new();

    public UpdateFetchTests()
    {
        _stagingRoot = Path.Combine(_root, "staging-root");
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Hash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>Builds a package the way the packaging script does, and serves it with its checksum.</summary>
    private (string PackageUrl, string ChecksumUrl, byte[] Archive) Publish(params (string Path, string Content)[] files)
    {
        var entries = files
            .Select(f =>
            {
                var bytes = Encoding.UTF8.GetBytes(f.Content);
                return new { path = f.Path, sha256 = Hash(bytes), size = bytes.Length, bytes };
            })
            .ToList();

        var manifest = JsonSerializer.Serialize(new
        {
            version = "1.5.6",
            runtimeIdentifier = "win-x64",
            generated = "2026-09-11T00:00:00Z",
            files = entries.Select(e => new { e.path, e.sha256, e.size }),
        });

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("update-manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(manifest);
            }

            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.path);
                using var stream = zipEntry.Open();
                stream.Write(entry.bytes);
            }
        }

        var archiveBytes = buffer.ToArray();
        _server.Add("/pkg.zip", archiveBytes);
        _server.Add("/pkg.zip.sha256", Encoding.ASCII.GetBytes(Hash(archiveBytes) + "  pkg.zip\n"));

        return ($"{_server.BaseUrl}/pkg.zip", $"{_server.BaseUrl}/pkg.zip.sha256", archiveBytes);
    }

    private Task<UpdateFetch.Result> Fetch(string packageUrl, string? checksumUrl, long expected, HttpClient? http = null) =>
        UpdateFetch.FetchAsync(
            http ?? new HttpClient(),
            packageUrl,
            checksumUrl,
            expected,
            "1.5.6",
            _stagingRoot);

    [Fact]
    public async Task APublishedPackageIsDownloadedVerifiedAndStaged()
    {
        var (packageUrl, checksumUrl, archive) = Publish(
            ("AdamCodexHub.App.dll", "the new app"),
            ("Assets/icon.txt", "an asset"));

        var result = await Fetch(packageUrl, checksumUrl, archive.Length);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.StagingDirectory);
        Assert.Equal("the new app", File.ReadAllText(Path.Combine(result.StagingDirectory!, "AdamCodexHub.App.dll")));
        Assert.Equal("an asset", File.ReadAllText(Path.Combine(result.StagingDirectory!, "Assets", "icon.txt")));
        Assert.True(File.Exists(Path.Combine(result.StagingDirectory!, "update-manifest.json")));

        // The archive itself is not left behind - only what was verified from it.
        Assert.False(File.Exists(Path.Combine(_stagingRoot, "1.5.6", "update.zip")));
    }

    [Fact]
    public async Task ATransferThatStoppedShortIsRefusedAndLeavesNothingBehind()
    {
        var (packageUrl, checksumUrl, archive) = Publish(("AdamCodexHub.App.dll", "the new app"));

        // What the release says it weighs against what arrived.
        var result = await Fetch(packageUrl, checksumUrl, archive.Length + 4096);

        Assert.False(result.Succeeded);
        Assert.Contains("stopped early", result.Message);
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "1.5.6")));
    }

    [Fact]
    public async Task APackageThatDoesNotMatchItsPublishedChecksumIsRefused()
    {
        var (packageUrl, _, archive) = Publish(("AdamCodexHub.App.dll", "the new app"));

        // A published digest of the right shape that is simply not this file's. (A missing checksum file
        // would not do: the code deliberately tolerates that, because the per-file digests still apply.)
        _server.Add("/wrong.sha256", Encoding.ASCII.GetBytes(new string('a', 64) + "  pkg.zip\n"));

        var result = await Fetch(packageUrl, $"{_server.BaseUrl}/wrong.sha256", archive.Length);

        Assert.False(result.Succeeded);
        Assert.Contains("published SHA-256", result.Message);
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "1.5.6")));
    }

    [Fact]
    public async Task AFileInsideThePackageThatDoesNotMatchTheManifestIsRefused()
    {
        // The archive is rewrapped so one entry no longer hashes to what the manifest promises. This is
        // the case the outer checksum cannot catch on its own, which is why both exist.
        var (_, _, _) = Publish(("AdamCodexHub.App.dll", "the new app"));

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = JsonSerializer.Serialize(new
            {
                version = "1.5.6",
                runtimeIdentifier = "win-x64",
                generated = "2026-09-11T00:00:00Z",
                files = new[]
                {
                    new { path = "AdamCodexHub.App.dll", sha256 = Hash(Encoding.UTF8.GetBytes("the new app")), size = 11 },
                },
            });

            using (var writer = new StreamWriter(archive.CreateEntry("update-manifest.json").Open(), new UTF8Encoding(false)))
            {
                writer.Write(manifest);
            }

            using var stream = archive.CreateEntry("AdamCodexHub.App.dll").Open();
            var tampered = Encoding.UTF8.GetBytes("something else");
            stream.Write(tampered);
        }

        var bytes = buffer.ToArray();
        _server.Add("/tampered.zip", bytes);
        _server.Add("/tampered.zip.sha256", Encoding.ASCII.GetBytes(Hash(bytes)));

        var result = await Fetch($"{_server.BaseUrl}/tampered.zip", $"{_server.BaseUrl}/tampered.zip.sha256", bytes.Length);

        Assert.False(result.Succeeded);
        Assert.Contains("did not match the manifest", result.Message);
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "1.5.6")));
    }

    [Fact]
    public async Task APackageWithNoManifestIsRefused()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("AdamCodexHub.App.dll").Open();
            stream.Write(Encoding.UTF8.GetBytes("no manifest here"));
        }

        var bytes = buffer.ToArray();
        _server.Add("/nomanifest.zip", bytes);

        var result = await Fetch($"{_server.BaseUrl}/nomanifest.zip", null, bytes.Length);

        Assert.False(result.Succeeded);
        Assert.Contains("no manifest", result.Message);
    }
}
