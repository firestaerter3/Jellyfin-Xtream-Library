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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Swaps a programme's wide Gracenote image for the portrait poster of the same asset, when
/// Gracenote has one.
/// <para>
/// Guides built on Gracenote data (epg.guru, for one) give a film a portrait poster
/// (<c>p35634_v_v8_ad.jpg</c>) but a series or show a wide 4:3 or 16:9 image
/// (<c>p29642504_b_h9_ad.jpg</c>). Jellyfin stores whatever the guide gives as the programme's
/// Primary image, and clients draw Primary in a portrait card, so the wide images come out with
/// their sides cut off. For a good part of those assets the same CDN serves a real portrait
/// poster under the name with <c>_b_v8_</c> in place of <c>_b_h9_</c> or <c>_b_h6_</c>. That name
/// cannot be told to exist without asking, so every candidate is checked once with a HEAD request
/// and the answer is kept on disk.
/// </para>
/// <para>
/// A failed check (timeout, 5xx, no network) is not recorded and the guide keeps the original
/// image; nothing here can make the guide fail.
/// </para>
/// </summary>
internal sealed class PortraitProgramImageResolver : IDisposable
{
    /// <summary>
    /// The most new checks one guide build makes. The first build after an install sees a few
    /// thousand images; spreading them over several builds keeps one build from waiting minutes.
    /// </summary>
    internal const int MaxLookupsPerRun = 600;

    /// <summary>
    /// How long "this asset has no portrait poster" holds before it is asked again.
    /// </summary>
    internal static readonly TimeSpan NegativeRecheck = TimeSpan.FromDays(7);

    /// <summary>
    /// How long "this asset has a portrait poster" holds before it is asked again.
    /// </summary>
    internal static readonly TimeSpan PositiveRecheck = TimeSpan.FromDays(30);

    /// <summary>
    /// Entries not checked for this long belong to programmes that left the guide, and are
    /// dropped when the file is written. Longer than <see cref="PositiveRecheck"/>, so an image
    /// still in use has always been checked again before this.
    /// </summary>
    internal static readonly TimeSpan PruneAge = TimeSpan.FromDays(60);

    private const int MaxParallelLookups = 4;

    private static readonly Regex LandscapePattern = new(
        @"^(?<stem>https?://[a-z0-9.-]*tmsimg\.com/assets/p\d+)_b_h(?:6|9)_(?<rev>[a-z]{2})\.jpg$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly Regex IconSrcPattern = new(
        "<icon\\b[^>]*?\\bsrc=\"(?<src>[^\"]+)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _cacheFile;
    private readonly ILogger _logger;
    private readonly Func<string, CancellationToken, Task<bool?>> _probe;
    private readonly HttpClient? _ownedClient;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="PortraitProgramImageResolver"/> class that
    /// checks candidates over HTTP.
    /// </summary>
    /// <param name="cacheFile">Where the answers are kept.</param>
    /// <param name="logger">The logger.</param>
    public PortraitProgramImageResolver(string cacheFile, ILogger logger)
    {
        _cacheFile = cacheFile;
        _logger = logger;
        _ownedClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _probe = ProbeAsync;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PortraitProgramImageResolver"/> class with
    /// a given check, for tests.
    /// </summary>
    /// <param name="cacheFile">Where the answers are kept.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="probe">Returns true when the url exists, false when it does not, null when
    /// that could not be told.</param>
    internal PortraitProgramImageResolver(string cacheFile, ILogger logger, Func<string, CancellationToken, Task<bool?>> probe)
    {
        _cacheFile = cacheFile;
        _logger = logger;
        _probe = probe;
    }

    /// <summary>
    /// Returns the portrait name for a wide Gracenote image url, or null when the url is not one.
    /// </summary>
    /// <param name="url">An image url from the guide.</param>
    /// <returns>The candidate url.</returns>
    internal static string? PortraitCandidate(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var m = LandscapePattern.Match(url);
        return m.Success ? m.Groups["stem"].Value + "_b_v8_" + m.Groups["rev"].Value + ".jpg" : null;
    }

    /// <summary>
    /// Checks the wide Gracenote images in <paramref name="xmltvDocuments"/> that have no fresh
    /// answer yet, at most <see cref="MaxLookupsPerRun"/> of them, and saves the answers.
    /// </summary>
    /// <param name="xmltvDocuments">The guide documents the next rewrite will read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ResolveAsync(IEnumerable<string> xmltvDocuments, CancellationToken cancellationToken)
    {
        try
        {
            Load();
            var now = DateTimeOffset.UtcNow;
            var due = xmltvDocuments
                .SelectMany(doc => IconSrcPattern.Matches(doc).Select(m => m.Groups["src"].Value))
                .Distinct(StringComparer.Ordinal)
                .Select(url => (Url: url, Candidate: PortraitCandidate(url)))
                .Where(x => x.Candidate != null && !IsFresh(x.Url, now))
                .ToList();
            if (due.Count == 0)
            {
                return;
            }

            var batch = due.Take(MaxLookupsPerRun).ToList();
            var found = 0;
            var missing = 0;
            var unknown = 0;
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLookups, CancellationToken = cancellationToken },
                async (item, ct) =>
                {
                    bool? exists;
                    try
                    {
                        exists = await _probe(item.Candidate!, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !ct.IsCancellationRequested)
                    {
                        exists = null;
                    }

                    if (exists is null)
                    {
                        Interlocked.Increment(ref unknown);
                        return;
                    }

                    _entries[item.Url] = new Entry(exists.Value ? item.Candidate : null, now.ToUnixTimeSeconds());
                    if (exists.Value)
                    {
                        Interlocked.Increment(ref found);
                    }
                    else
                    {
                        Interlocked.Increment(ref missing);
                    }
                }).ConfigureAwait(false);

            _logger.LogInformation(
                "Portrait programme images: checked {Checked} of {Due} wide images, {Found} have a portrait poster, {Missing} have none, {Unknown} could not be checked",
                batch.Count,
                due.Count,
                found,
                missing,
                unknown);
            Save(now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check programme images for a portrait poster; the guide keeps the original images");
        }
    }

    /// <summary>
    /// Returns the portrait poster recorded for <paramref name="url"/>, or the url itself.
    /// </summary>
    /// <param name="url">An image url from the guide.</param>
    /// <returns>The url to write to the guide.</returns>
    public string Rewrite(string url)
        => _entries.TryGetValue(url, out var e) && e.Portrait != null ? e.Portrait : url;

    /// <inheritdoc />
    public void Dispose() => _ownedClient?.Dispose();

    private bool IsFresh(string url, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(url, out var e))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(e.CheckedUnix);
        return age < (e.Portrait != null ? PositiveRecheck : NegativeRecheck);
    }

    private async Task<bool?> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await _ownedClient!.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone ? false : null;
    }

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(_cacheFile))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_cacheFile), JsonOptions);
            foreach (var (key, value) in stored ?? new Dictionary<string, Entry>())
            {
                _entries[key] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {File}; checking programme images again", _cacheFile);
        }
    }

    private void Save(DateTimeOffset now)
    {
        var cutoff = (now - PruneAge).ToUnixTimeSeconds();
        foreach (var key in _entries.Where(p => p.Value.CheckedUnix < cutoff).Select(p => p.Key).ToList())
        {
            _entries.TryRemove(key, out _);
        }

        var dir = Path.GetDirectoryName(_cacheFile);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = _cacheFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, Entry>(_entries, StringComparer.Ordinal), JsonOptions));
        File.Move(tmp, _cacheFile, overwrite: true);
    }

    /// <summary>
    /// One recorded answer.
    /// </summary>
    /// <param name="Portrait">The portrait poster url, or null when there is none.</param>
    /// <param name="CheckedUnix">When it was checked, in Unix seconds.</param>
    internal sealed record Entry(string? Portrait, long CheckedUnix);
}
