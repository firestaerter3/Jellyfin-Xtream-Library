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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// Provider credentials must not reach Jellyfin's log, which users paste into issues.
/// </summary>
public class StreamUrlRedactorTests
{
    [Theory]
    [InlineData("http://host:8080/live/alice/opensesame/123.ts", "http://host:8080/live/***/***/123.ts")]
    [InlineData("http://host/movie/alice/opensesame/55.mkv", "http://host/movie/***/***/55.mkv")]
    [InlineData("http://host/series/alice/opensesame/77.mp4", "http://host/series/***/***/77.mp4")]
    [InlineData("http://host/timeshift/alice/opensesame/60/2026-10-11:20-00/9.ts", "http://host/timeshift/***/***/60/2026-10-11:20-00/9.ts")]
    [InlineData("http://host/LIVE/alice/opensesame/1.m3u8", "http://host/LIVE/***/***/1.m3u8")]
    [InlineData("http://host/live/alice/open?sesame/123.ts", "http://host/live/***/***/123.ts")]
    [InlineData("http://host/live/alice/open#sesame/123.ts", "http://host/live/***/***/123.ts")]
    [InlineData("http://host/live/al?ice/opensesame/123.ts", "http://host/live/***/***/123.ts")]
    [InlineData("http://host/live//opensesame/123.ts", "http://host/live/***/***/123.ts")]
    [InlineData("http://host/live/live/alice/opensesame/123.ts", "http://host/live/live/***/***/123.ts")]
    [InlineData("http://host/movie/movie/alice/opensesame/5.mkv", "http://host/movie/movie/***/***/5.mkv")]
    [InlineData("http://host/live/timeshift/alice/opensesame/60/2026-10-11:20-00/9.ts", "http://host/live/timeshift/***/***/60/2026-10-11:20-00/9.ts")]
    [InlineData("http://host/live/alice/opensesame/123.ts?token=x", "http://host/live/***/***/123.ts?token=x")]
    [InlineData("http://host/live/live/alice/opensesame/123.ts/", "http://host/live/live/***/***/123.ts/")]
    [InlineData("{\"user_info\":{\"username\":\"alice\",\"password\":\"opensesame\",\"status\":\"Active\"}}", "{\"user_info\":{\"username\":\"***\",\"password\":\"***\",\"status\":\"Active\"}}")]
    [InlineData("{\"user_info\":{\"password\" : \"opens", "{\"user_info\":{\"password\" : \"***\"")]
    [InlineData("http://host/player_api.php?username=alice&password=opensesame&action=get_vod_streams", "http://host/player_api.php?username=***&password=***&action=get_vod_streams")]
    [InlineData("http://host/player_api.php?action=get_series_info&password=opensesame&username=alice", "http://host/player_api.php?action=get_series_info&password=***&username=***")]
    public void Redact_HidesUsernameAndPassword(string url, string expected)
    {
        StreamUrlRedactor.Redact(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("http://dispatcharr:9191/api/vod/movies/12/providers/")]
    [InlineData("http://dispatcharr:9191/api/vod/series/12/provider-info/")]
    [InlineData("http://host/proxy/ts/stream/3f2a")]
    [InlineData("/config/xtream-library/Movies/Live Free (2007)/Live Free (2007).strm")]
    public void Redact_LeavesOtherUrlsAlone(string url)
    {
        StreamUrlRedactor.Redact(url).Should().Be(url);
    }

    [Fact]
    public void Redact_NullOrEmpty_ReturnsEmpty()
    {
        StreamUrlRedactor.Redact((string?)null).Should().BeEmpty();
        StreamUrlRedactor.Redact((Uri?)null).Should().BeEmpty();
        StreamUrlRedactor.Redact(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Redact_Uri_UsesTheSameRules()
    {
        StreamUrlRedactor.Redact(new Uri("http://host/player_api.php?username=alice&password=opensesame"))
            .Should().Be("http://host/player_api.php?username=***&password=***");
    }

    [Fact]
    public async Task LiveStreamOpen_FailingStream_LogsNoCredentials()
    {
        var log = new List<string>();
        using var client = new HttpClient(new StatusHandler(HttpStatusCode.NotFound));
        var source = new MediaSourceInfo { Id = "1", Path = "http://host:8080/live/alice/opensesame/123.ts" };
        using var stream = new XtreamLiveStream(source, client, new ListLogger(log));

        // Open connects nothing since homelab-ops #550; the failure surfaces on the first read.
        await stream.Open(CancellationToken.None);
        var read = () => stream.GetStream();

        read.Should().Throw<HttpRequestException>();
        log.Should().NotBeEmpty();
        log.Should().OnlyContain(line => !line.Contains("alice", StringComparison.Ordinal)
            && !line.Contains("opensesame", StringComparison.Ordinal));
        log.Should().Contain(line => line.Contains("/live/***/***/123.ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveStreamOpen_DoesNotConnect()
    {
        // ffmpeg and the probe open the URL themselves; a connection made in Open was an extra
        // Dispatcharr client nobody read (homelab-ops #550).
        var handler = new StatusHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var source = new MediaSourceInfo { Id = "1", Path = "http://host:8080/live/u/p/123.ts" };
        using var stream = new XtreamLiveStream(source, client, new ListLogger(new List<string>()));

        await stream.Open(CancellationToken.None);

        handler.Requests.Should().Be(0);
    }

    [Fact]
    public void LiveStream_IsShared()
    {
        // Every open of a channel gets the same live stream id; unshared, stopping one session
        // closed the next one too (iOS -1100, homelab-ops #550).
        using var client = new HttpClient(new StatusHandler(HttpStatusCode.OK));
        using var stream = new XtreamLiveStream(new MediaSourceInfo { Id = "1", Path = "http://host/1.ts" }, client, new ListLogger(new List<string>()));

        stream.EnableStreamSharing.Should().BeTrue();
        stream.ConsumerCount.Should().Be(1, "the client that opened it is its first consumer");
    }

    [Fact]
    public async Task LiveStream_TwoConcurrentReads_ConnectOnce()
    {
        // Shared, so two consumers can read at once; two connections would take two slots on a
        // provider that may allow one (PR #167 review).
        var handler = new SlowHandler();
        using var client = new HttpClient(handler);
        using var stream = new XtreamLiveStream(new MediaSourceInfo { Id = "1", Path = "http://host/1.ts" }, client, new ListLogger(new List<string>()));

        var reads = Enumerable.Range(0, 4).Select(_ => Task.Run(() => stream.GetStream())).ToArray();
        var streams = await Task.WhenAll(reads);

        handler.Requests.Should().Be(1);
        streams.Distinct().Should().HaveCount(1);
    }

    [Fact]
    public void LiveStream_FailedConnect_DisposesTheResponse()
    {
        var content = new TrackedContent();
        using var client = new HttpClient(new FixedHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content }));
        using var stream = new XtreamLiveStream(new MediaSourceInfo { Id = "1", Path = "http://host/1.ts" }, client, new ListLogger(new List<string>()));

        var read = () => stream.GetStream();

        read.Should().Throw<HttpRequestException>();
        content.Disposed.Should().BeTrue("a failed response must not keep its connection open");
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[188]) };
        }
    }

    private sealed class FixedHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class TrackedContent : ByteArrayContent
    {
        public TrackedContent()
            : base(Array.Empty<byte>())
        {
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status));
        }

        public int Requests { get; private set; }
    }

    private sealed class ListLogger(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => sink.Add($"[{logLevel}] {formatter(state, exception)} {exception}");
    }
}
