using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>Integration tests for artwork downloads over localhost HTTP.</summary>
public class ArtworkHttpIntegrationTests
{
    [Fact]
    public async Task Http200DownloadsAndSavesArtwork()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-http-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serving = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var request = new byte[1];
                await stream.ReadExactlyAsync(request);
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(new byte[] { 1, 2, 3, 4 });
            });
            var destination = Path.Combine(root, "image.jpg");
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 3 };
            var url = $"http://127.0.0.1:{port}/image.jpg";
            var result = await StrmSyncService.DownloadImageCoreAsync(url, destination, tracker, config, NullLogger.Instance, CancellationToken.None);
            Assert.True(result);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
            Assert.False(tracker.IsBlocked(ArtworkFailureTracker.Hash(url)));
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task Http404BlocksAfterConfiguredThreshold()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-404-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var requests = 0;
            var serving = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    var stream = client.GetStream();
                    var firstByte = new byte[1];
                    await stream.ReadExactlyAsync(firstByte);
                    Interlocked.Increment(ref requests);
                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response);
                }
            });
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance, () => 2, () => 1);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 3 };
            var url = $"http://127.0.0.1:{port}/missing.jpg";
            var destination = Path.Combine(root, "missing.jpg");
            Assert.False(await StrmSyncService.DownloadImageCoreAsync(url, destination, tracker, config, NullLogger.Instance, CancellationToken.None));
            Assert.False(tracker.IsBlocked(ArtworkFailureTracker.Hash(url)));
            Assert.False(await StrmSyncService.DownloadImageCoreAsync(url, destination, tracker, config, NullLogger.Instance, CancellationToken.None));
            Assert.True(tracker.IsBlocked(ArtworkFailureTracker.Hash(url)));
            Assert.False(await StrmSyncService.DownloadImageCoreAsync(url, destination, tracker, config, NullLogger.Instance, CancellationToken.None));
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, requests);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task OversizedContentLengthRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-size-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serving = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                await stream.ReadExactlyAsync(new byte[1]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 11000000\r\nConnection: close\r\n\r\n"));
                await release.Task.WaitAsync(TimeSpan.FromSeconds(8));
            });
            var url = $"http://127.0.0.1:{port}/oversized.jpg";
            var destination = Path.Combine(root, "oversized.jpg");
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance, () => 1, () => 1);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 10 };
            Assert.False(await StrmSyncService.DownloadImageCoreAsync(url, destination, tracker, config, NullLogger.Instance, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(tracker.IsBlocked(ArtworkFailureTracker.Hash(url)));
            Assert.False(File.Exists(destination));
            release.TrySetResult(true);
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult(true);
            listener.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnknownLengthOversizeRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-unknown-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serving = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                await stream.ReadExactlyAsync(new byte[1]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n"));
                var chunk = new byte[81920];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try
                {
                    for (var i = 0; i < 130; i++) await stream.WriteAsync(chunk, timeout.Token);
                }
                catch (IOException) { }
                catch (OperationCanceledException) { }
            });
            var url = $"http://127.0.0.1:{port}/unbounded.jpg";
            var destination = Path.Combine(root, "unbounded.jpg");
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance, () => 1, () => 1);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 15 };
            Assert.False(await StrmSyncService.DownloadImageCoreAsync(url, destination, tracker, config, NullLogger.Instance, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.True(tracker.IsBlocked(ArtworkFailureTracker.Hash(url)));
            Assert.False(File.Exists(destination));
            await serving.WaitAsync(TimeSpan.FromSeconds(13));
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

}
