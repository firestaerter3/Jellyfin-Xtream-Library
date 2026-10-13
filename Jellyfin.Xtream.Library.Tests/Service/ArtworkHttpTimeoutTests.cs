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

/// <summary>Tests stalled artwork transfers and cancellation.</summary>
public class ArtworkHttpTimeoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledBodyHonorsTimeoutAndCancellation(bool explicitCancellation)
    {
        var root = Path.Combine(Path.GetTempPath(), "art-stall-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serving = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                await stream.ReadExactlyAsync(new byte[1]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(new byte[] { 42 });
                bodyStarted.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(8));
            });
            var url = $"http://127.0.0.1:{port}/stalled.jpg";
            var destination = Path.Combine(root, "stalled.jpg");
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance, () => 1, () => 1);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 3 };
            using var cancel = new CancellationTokenSource();
            var download = StrmSyncService.DownloadImageCoreAsync(url, destination, tracker, config, NullLogger.Instance, cancel.Token);
            await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (explicitCancellation) cancel.Cancel();
            Assert.False(await download.WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.Equal(!explicitCancellation, tracker.IsBlocked(ArtworkFailureTracker.Hash(url)));
            Assert.Equal(!explicitCancellation, File.Exists(Path.Combine(root, "xtream-library", "artwork_failures.jsonl")));
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
    public async Task PrematureDisconnectRecordsFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-eof-" + Guid.NewGuid().ToString("N"));
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
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(new byte[] { 42 });
            });
            var url = $"http://127.0.0.1:{port}/broken.jpg";
            var destination = Path.Combine(root, "broken.jpg");
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance, () => 1, () => 1);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 3 };
            Assert.False(await StrmSyncService.DownloadImageCoreAsync(url, destination, tracker, config, NullLogger.Instance, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6)));
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(tracker.IsBlocked(ArtworkFailureTracker.Hash(url)));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledHeadersHonorTimeoutAndCancellation(bool explicitCancellation)
    {
        var root = Path.Combine(Path.GetTempPath(), "art-headers-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serving = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await client.GetStream().ReadExactlyAsync(new byte[1]);
                received.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(8));
            });
            var url = $"http://127.0.0.1:{port}/headers.jpg";
            var destination = Path.Combine(root, "headers.jpg");
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance, () => 1, () => 1);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 3 };
            using var cancel = new CancellationTokenSource();
            var download = StrmSyncService.DownloadImageCoreAsync(url, destination, tracker, config, NullLogger.Instance, cancel.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (explicitCancellation) cancel.Cancel();
            Assert.False(await download.WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.Equal(!explicitCancellation, tracker.IsBlocked(ArtworkFailureTracker.Hash(url)));
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

}
