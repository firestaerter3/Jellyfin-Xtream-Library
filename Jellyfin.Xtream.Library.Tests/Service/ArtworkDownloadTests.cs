// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// GitHub #154: artwork from a host that does not answer held each image for 30 seconds, every
/// image, every sync.
/// </summary>
public class ArtworkDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xtream-artwork-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public ArtworkDownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Success_WritesTheImageAndLeavesNoTemporaryFile()
    {
        var handler = new Handler(_ => Ok(new byte[] { 1, 2, 3 }));

        var ok = await DownloadAsync(handler, "http://img.test/a.jpg", "poster.jpg");

        ok.Should().BeTrue();
        File.ReadAllBytes(Path.Combine(_dir, "poster.jpg")).Should().Equal(1, 2, 3);
        Directory.GetFiles(_dir).Should().ContainSingle("no .part file may be left behind");
    }

    [Fact]
    public async Task ExistingFile_IsNotDownloadedAgain()
    {
        File.WriteAllBytes(Path.Combine(_dir, "poster.jpg"), new byte[] { 9 });
        var handler = new Handler(_ => Ok(new byte[] { 1 }));

        var ok = await DownloadAsync(handler, "http://img.test/a.jpg", "poster.jpg");

        ok.Should().BeTrue();
        handler.Requests.Should().Be(0);
    }

    [Fact]
    public async Task HostThatDoesNotAnswer_IsSkippedAfterFiveFailuresInARow()
    {
        var handler = new Handler(_ => throw new HttpRequestException("connection refused"));
        var breaker = new ArtworkHostBreaker();

        for (int i = 0; i < 20; i++)
        {
            (await DownloadAsync(handler, $"http://dead.test/{i}.jpg", $"{i}.jpg", breaker)).Should().BeFalse();
        }

        handler.Requests.Should().Be(ArtworkHostBreaker.FailureThreshold, "after the fifth failure the host is not asked again");
        _log.Count(l => l.Contains("dead.test", StringComparison.Ordinal) && l.StartsWith("[Warning]", StringComparison.Ordinal))
            .Should().Be(1, "skipping a host is logged once");
    }

    [Fact]
    public async Task OtherHosts_AreNotAffectedByOneDeadHost()
    {
        var handler = new Handler(r => r.RequestUri!.Host == "dead.test"
            ? throw new HttpRequestException("connection refused")
            : Ok(new byte[] { 1 }));
        var breaker = new ArtworkHostBreaker();

        for (int i = 0; i < 10; i++)
        {
            await DownloadAsync(handler, $"http://dead.test/{i}.jpg", $"dead{i}.jpg", breaker);
        }

        (await DownloadAsync(handler, "http://alive.test/a.jpg", "alive.jpg", breaker)).Should().BeTrue();
    }

    [Fact]
    public async Task MissingImages_DoNotSkipTheHost()
    {
        // A 404 is an answer: the host is up, only that file is gone.
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var breaker = new ArtworkHostBreaker();

        for (int i = 0; i < 10; i++)
        {
            await DownloadAsync(handler, $"http://img.test/{i}.jpg", $"{i}.jpg", breaker);
        }

        handler.Requests.Should().Be(10);
        breaker.IsSkipped("img.test").Should().BeFalse();
    }

    [Fact]
    public async Task ServerErrors_CountAsFailures()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var breaker = new ArtworkHostBreaker();

        for (int i = 0; i < 10; i++)
        {
            await DownloadAsync(handler, $"http://img.test/{i}.jpg", $"{i}.jpg", breaker);
        }

        handler.Requests.Should().Be(ArtworkHostBreaker.FailureThreshold);
    }

    [Fact]
    public async Task Timeout_CountsAsAFailure()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Ok(new byte[] { 1 });
        });
        var breaker = new ArtworkHostBreaker();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };

        for (int i = 0; i < ArtworkHostBreaker.FailureThreshold; i++)
        {
            (await StrmSyncService.DownloadImageCoreAsync(client, breaker, $"http://slow.test/{i}.jpg", Path.Combine(_dir, $"{i}.jpg"), new ListLogger(_log), CancellationToken.None))
                .Should().BeFalse();
        }

        breaker.IsSkipped("slow.test").Should().BeTrue();
    }

    [Fact]
    public async Task ServerThatStallsHalfwayThroughTheImage_IsCutOffAndCounted()
    {
        // The headers arrive at once; the body never finishes. HttpClient.Timeout alone does not
        // cover this once the headers are in.
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        var breaker = new ArtworkHostBreaker();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ok = await StrmSyncService.DownloadImageCoreAsync(client, breaker, "http://stall.test/a.jpg", Path.Combine(_dir, "a.jpg"), new ListLogger(_log), CancellationToken.None);

        ok.Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        Directory.GetFiles(_dir).Should().BeEmpty();
        for (int i = 1; i < ArtworkHostBreaker.FailureThreshold; i++)
        {
            await StrmSyncService.DownloadImageCoreAsync(client, breaker, $"http://stall.test/{i}.jpg", Path.Combine(_dir, $"{i}.jpg"), new ListLogger(_log), CancellationToken.None);
        }

        breaker.IsSkipped("stall.test").Should().BeTrue();
    }

    [Fact]
    public async Task ConnectionDroppedHalfwayThroughTheImage_CountsAsAFailure()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream()) });
        var breaker = new ArtworkHostBreaker();

        for (int i = 0; i < 10; i++)
        {
            (await DownloadAsync(handler, $"http://drop.test/{i}.jpg", $"{i}.jpg", breaker)).Should().BeFalse();
        }

        handler.Requests.Should().Be(ArtworkHostBreaker.FailureThreshold);
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public async Task StoppingTheSync_IsNotCountedAgainstTheHost()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Ok(new byte[] { 1 });
        });
        var breaker = new ArtworkHostBreaker();
        using var client = new HttpClient(handler);

        for (int i = 0; i < 10; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            await StrmSyncService.DownloadImageCoreAsync(client, breaker, $"http://img.test/{i}.jpg", Path.Combine(_dir, $"{i}.jpg"), new ListLogger(_log), cts.Token);
        }

        breaker.IsSkipped("img.test").Should().BeFalse();
    }

    [Fact]
    public async Task ImageLargerThanTheCap_IsRefusedEvenWithoutContentLength()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnboundedStream(StrmSyncService.MaxArtworkBytes + 1)),
        });

        var ok = await DownloadAsync(handler, "http://img.test/huge.jpg", "huge.jpg");

        ok.Should().BeFalse();
        Directory.GetFiles(_dir).Should().BeEmpty("neither the image nor its temporary file may remain");
    }

    [Fact]
    public async Task ImageAnnouncedLargerThanTheCap_IsRefused()
    {
        var handler = new Handler(_ =>
        {
            var response = Ok(new byte[] { 1 });
            response.Content.Headers.ContentLength = StrmSyncService.MaxArtworkBytes + 1;
            return response;
        });

        (await DownloadAsync(handler, "http://img.test/huge.jpg", "huge.jpg")).Should().BeFalse();
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public async Task TwoDownloadsOfTheSameFile_LeaveOneCompleteImage()
    {
        var handler = new Handler(_ => Ok(Enumerable.Repeat((byte)7, 50_000).ToArray()));
        var breaker = new ArtworkHostBreaker();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            DownloadAsync(handler, $"http://img.test/{i}.jpg", "same.jpg", breaker)));

        results.Should().OnlyContain(r => r);
        File.ReadAllBytes(Path.Combine(_dir, "same.jpg")).Should().HaveCount(50_000).And.OnlyContain(b => b == 7);
        Directory.GetFiles(_dir).Should().ContainSingle();
    }

    [Fact]
    public void ImageClient_HasTheShortTimeouts()
    {
        using var client = StrmSyncService.CreateImageHttpClient();

        client.Timeout.Should().Be(StrmSyncService.ArtworkTotalTimeout);
        StrmSyncService.ArtworkTotalTimeout.Should().BeLessThan(TimeSpan.FromSeconds(30));
        StrmSyncService.ArtworkConnectTimeout.Should().BeLessThan(StrmSyncService.ArtworkTotalTimeout);
    }

    [Fact]
    public void Breaker_AnswerResetsTheCountButNotATrippedHost()
    {
        var breaker = new ArtworkHostBreaker();
        for (int i = 0; i < ArtworkHostBreaker.FailureThreshold - 1; i++)
        {
            breaker.RecordFailure("h");
        }

        breaker.RecordAnswer("h");
        breaker.RecordFailure("h");
        breaker.IsSkipped("h").Should().BeFalse("the run of failures was broken by an answer");

        for (int i = 0; i < ArtworkHostBreaker.FailureThreshold; i++)
        {
            breaker.RecordFailure("h");
        }

        breaker.RecordAnswer("h");
        breaker.IsSkipped("h").Should().BeTrue("a late answer from a download started earlier does not reopen the host");
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private async Task<bool> DownloadAsync(Handler handler, string url, string fileName, ArtworkHostBreaker? breaker = null)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        return await StrmSyncService.DownloadImageCoreAsync(
            client, breaker ?? new ArtworkHostBreaker(), url, Path.Combine(_dir, fileName), new ListLogger(_log), CancellationToken.None);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private int _requests;

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => _respond = (r, _) => Task.FromResult(respond(r));

        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => _respond = respond;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return _respond(request, cancellationToken);
        }
    }

    // A response body of a given length that never announces it.
    private sealed class UnboundedStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Sends a few bytes, then waits until it is cancelled.
    private sealed class StallingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = 1;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Sends a few bytes, then the connection drops.
    private sealed class DroppingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_sent)
            {
                _sent = true;
                buffer[offset] = 1;
                return 1;
            }

            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ListLogger(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (sink)
            {
                sink.Add($"[{logLevel}] {formatter(state, exception)}");
            }
        }
    }
}
