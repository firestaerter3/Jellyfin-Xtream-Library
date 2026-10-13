using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>Tests artwork HTTP connection limits.</summary>
public class ArtworkHostLimitTests
{
    [Fact]
    public async Task FourthSameHostRequestWaitsForConnection()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-host-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var releases = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var third = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = 0;
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var urls = Enumerable.Range(0, 256).Select(i => $"http://127.0.0.1:{port}/{i}.jpg").GroupBy(u => Convert.ToInt32(ArtworkFailureTracker.Hash(u)[..2], 16) % 64).Take(4).Select(g => g.First()).ToArray();
            Assert.Equal(4, urls.Length);
            var serving = Task.Run(async () =>
            {
                var handlers = new List<Task>();
                for (var i = 0; i < 4; i++)
                {
                    var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    handlers.Add(RespondAsync(client, releases[i].Task));
                    Interlocked.Increment(ref accepted);
                    if (i == 2) third.TrySetResult(true);
                    if (i == 3) fourth.TrySetResult(true);
                }

                await Task.WhenAll(handlers);
            });
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 15 };
            var downloads = urls.Select((url, i) => StrmSyncService.DownloadImageCoreAsync(url, Path.Combine(root, $"{i}.jpg"), tracker, config, NullLogger.Instance, CancellationToken.None)).ToArray();
            await third.Task.WaitAsync(TimeSpan.FromSeconds(6));
            await Task.Delay(300);
            Assert.Equal(3, Volatile.Read(ref accepted));
            releases[0].TrySetResult(true);
            await fourth.Task.WaitAsync(TimeSpan.FromSeconds(6));
            foreach (var release in releases) release.TrySetResult(true);
            Assert.All(await Task.WhenAll(downloads).WaitAsync(TimeSpan.FromSeconds(8)), Assert.True);
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (var release in releases) release.TrySetResult(true);
            listener.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DifferentHostsDownloadIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-hosts-" + Guid.NewGuid().ToString("N"));
        var slow = new TcpListener(IPAddress.Loopback, 0);
        var fast = new TcpListener(IPAddress.Loopback, 0);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.Start();
        fast.Start();
        try
        {
            var slowPort = ((IPEndPoint)slow.LocalEndpoint).Port;
            var fastPort = ((IPEndPoint)fast.LocalEndpoint).Port;
            var slowUrls = Enumerable.Range(0, 256).Select(i => $"http://127.0.0.1:{slowPort}/{i}.jpg").GroupBy(u => Convert.ToInt32(ArtworkFailureTracker.Hash(u)[..2], 16) % 64).Take(3).Select(g => g.First()).ToArray();
            Assert.Equal(3, slowUrls.Length);
            var stripes = slowUrls.Select(u => Convert.ToInt32(ArtworkFailureTracker.Hash(u)[..2], 16) % 64).ToHashSet();
            var fastUrl = Enumerable.Range(0, 256).Select(i => $"http://127.0.0.1:{fastPort}/{i}.jpg").First(u => !stripes.Contains(Convert.ToInt32(ArtworkFailureTracker.Hash(u)[..2], 16) % 64));
            var slowServer = Task.Run(async () =>
            {
                var handlers = new List<Task>();
                for (var i = 0; i < 3; i++)
                {
                    var client = await slow.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    handlers.Add(RespondAsync(client, release.Task));
                }

                ready.TrySetResult(true);
                await Task.WhenAll(handlers);
            });
            var fastServer = Task.Run(async () =>
            {
                var client = await fast.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
                await RespondAsync(client, Task.CompletedTask);
            });
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 15 };
            var pending = slowUrls.Select((url, i) => StrmSyncService.DownloadImageCoreAsync(url, Path.Combine(root, $"slow{i}.jpg"), tracker, config, NullLogger.Instance, CancellationToken.None)).ToArray();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(6));
            var fastResult = await StrmSyncService.DownloadImageCoreAsync(fastUrl, Path.Combine(root, "fast.jpg"), tracker, config, NullLogger.Instance, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fastResult);
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            release.TrySetResult(true);
            Assert.All(await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(8)), Assert.True);
            await Task.WhenAll(slowServer, fastServer).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult(true);
            slow.Stop();
            fast.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancelledHostSlotWaitDoesNotBlacklist()
    {
        var root = Path.Combine(Path.GetTempPath(), "art-cancel-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var urls = Enumerable.Range(0, 256).Select(i => $"http://127.0.0.1:{port}/{i}.jpg").GroupBy(u => Convert.ToInt32(ArtworkFailureTracker.Hash(u)[..2], 16) % 64).Take(4).Select(g => g.First()).ToArray();
            Assert.Equal(4, urls.Length);
            var server = Task.Run(async () =>
            {
                var handlers = new List<Task>();
                for (var i = 0; i < 3; i++)
                {
                    var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    handlers.Add(RespondAsync(client, release.Task));
                }
                ready.TrySetResult(true);
                await Task.WhenAll(handlers);
            });
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance, () => 1, () => 1);
            var config = new PluginConfiguration { ArtworkConnectionTimeoutSeconds = 3, ArtworkDownloadTimeoutSeconds = 15 };
            var active = urls.Take(3).Select((url, i) => StrmSyncService.DownloadImageCoreAsync(url, Path.Combine(root, $"active{i}.jpg"), tracker, config, NullLogger.Instance, CancellationToken.None)).ToArray();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(6));
            using var cancel = new CancellationTokenSource();
            var destination = Path.Combine(root, "cancelled.jpg");
            var waiting = StrmSyncService.DownloadImageCoreAsync(urls[3], destination, tracker, config, NullLogger.Instance, cancel.Token);
            await Task.Delay(250);
            Assert.False(waiting.IsCompleted);
            cancel.Cancel();
            Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.False(tracker.IsBlocked(ArtworkFailureTracker.Hash(urls[3])));
            Assert.False(File.Exists(destination));
            Assert.All(active, task => Assert.False(task.IsCompleted));
            release.TrySetResult(true);
            Assert.All(await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(8)), Assert.True);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult(true);
            listener.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task RespondAsync(TcpClient client, Task release)
    {
        using (client)
        {
            var stream = client.GetStream();
            await stream.ReadExactlyAsync(new byte[1]);
            await release.WaitAsync(TimeSpan.FromSeconds(12));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(new byte[] { 7 });
        }
    }
}
