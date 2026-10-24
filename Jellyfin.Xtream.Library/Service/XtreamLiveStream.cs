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
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Manages the lifecycle of a single HTTP live stream connection for the Xtream tuner host.
/// </summary>
internal sealed class XtreamLiveStream : ILiveStream, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private readonly object _connectLock = new();
    private HttpResponseMessage? _response;
    private Stream? _stream;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="XtreamLiveStream"/> class.
    /// </summary>
    /// <param name="mediaSource">The media source info with stream URL in Path.</param>
    /// <param name="httpClient">The HTTP client for streaming.</param>
    /// <param name="logger">The logger instance.</param>
    public XtreamLiveStream(MediaSourceInfo mediaSource, HttpClient httpClient, ILogger logger)
    {
        MediaSource = mediaSource;
        _httpClient = httpClient;
        _logger = logger;
        UniqueId = Guid.NewGuid().ToString("N");
        TunerHostId = XtreamTunerHost.TunerType;
        OriginalStreamId = mediaSource.Id;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Starts at 1, for the client that opened it, as Jellyfin's own tuner streams do. Jellyfin
    /// only increments it when another client reuses the stream and closes the stream when a stop
    /// brings it to 0; starting at 0, the first stop of two sessions closed both
    /// (homelab-ops #550, logged as "consumer count is now -1" before sharing was on).
    /// </remarks>
    public int ConsumerCount { get; set; } = 1;

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Shared, as Jellyfin's own M3U tuner does. Jellyfin names a live stream after its source, so
    /// every open of a channel gets the same id. Unshared, a client that opens a channel twice
    /// and then stops the first session (the iOS app does this) closed the second one as well:
    /// ffmpeg stopped, the playlist was deleted, and the app failed with -1100. Shared, the second
    /// open reuses the stream and only the consumer count changes. Safe because <see cref="Open"/>
    /// connects nothing (homelab-ops #550).
    /// </remarks>
    public bool EnableStreamSharing => true;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Connects nothing (homelab-ops #550). ffmpeg and the probe open the URL themselves, so a
    /// connection made here was only ever an extra Dispatcharr client that nobody read. Opening
    /// still matters: it gives Jellyfin a live stream id at once, which is what lets it start
    /// ffmpeg straight away (Emby's plugin does the same). The connection is made on first
    /// <see cref="GetStream"/>, for callers that do read through the plugin.
    /// </remarks>
    public Task Open(CancellationToken openCancellationToken)
    {
        _logger.LogDebug("Live stream {UniqueId} opened; connecting on first read", UniqueId);
        return Task.CompletedTask;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Opening stream: {Url}", StreamUrlRedactor.Redact(MediaSource.Path));
        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.GetAsync(
                MediaSource.Path,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            _stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            _response = response;
            _logger.LogInformation("Stream opened successfully (HTTP {StatusCode})", (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "Failed to open stream {Url} — HTTP {StatusCode}: {Reason}. Check BaseUrl, Username, and Password in plugin settings.",
                StreamUrlRedactor.Redact(MediaSource.Path),
                (int?)ex.StatusCode,
                ex.Message);
            throw;
        }
        finally
        {
            // A response that did not become the stream (failed status, timeout) is not kept, so a
            // retry cannot leave its connection open.
            if (!ReferenceEquals(response, _response))
            {
                response?.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public Task Close()
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Connects under a lock: the stream is shared, so two consumers can get here at once, and two
    /// connections would take two slots on a provider that may allow one.
    /// </remarks>
    public Stream GetStream()
    {
        lock (_connectLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is null)
            {
                // Bounded, because Dispose waits on the same lock: a stalled provider must not hold
                // up closing the session for HttpClient's default 100 s.
                using var timeout = new CancellationTokenSource(ConnectTimeout);
                ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            }

            return _stream!;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Takes the same lock as <see cref="GetStream"/>, so a connect in progress finishes first and
    /// its stream is disposed here rather than left open.
    /// </remarks>
    public void Dispose()
    {
        lock (_connectLock)
        {
            if (!_disposed)
            {
                _stream?.Dispose();
                _response?.Dispose();
                _disposed = true;
            }
        }
    }
}
