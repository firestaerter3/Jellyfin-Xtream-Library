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

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Manages the lifecycle of a single HTTP live stream connection for the Xtream tuner host.
/// </summary>
internal sealed class XtreamLiveStream : ILiveStream, IDisposable
{
    private readonly HttpClient _httpClient;
    private HttpResponseMessage? _response;
    private Stream? _stream;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="XtreamLiveStream"/> class.
    /// </summary>
    /// <param name="mediaSource">The media source info with stream URL in Path.</param>
    /// <param name="httpClient">The HTTP client for streaming.</param>
    public XtreamLiveStream(MediaSourceInfo mediaSource, HttpClient httpClient)
    {
        MediaSource = mediaSource;
        _httpClient = httpClient;
        UniqueId = Guid.NewGuid().ToString("N");
        TunerHostId = XtreamTunerHost.TunerType;
        OriginalStreamId = mediaSource.Id;
    }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId { get; }

    /// <inheritdoc />
    public bool EnableStreamSharing => false;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        _response = await _httpClient.GetAsync(
            MediaSource.Path,
            HttpCompletionOption.ResponseHeadersRead,
            openCancellationToken).ConfigureAwait(false);
        _response.EnsureSuccessStatusCode();
        _stream = await _response.Content.ReadAsStreamAsync(openCancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task Close()
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        return _stream ?? throw new InvalidOperationException("Stream not opened. Call Open() first.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _stream?.Dispose();
            _response?.Dispose();
            _disposed = true;
        }
    }
}
