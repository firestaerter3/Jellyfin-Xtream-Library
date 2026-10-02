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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Keeps the guide of catch-up channels after the provider has dropped it (GitHub #108).
/// <para>
/// Providers send about a day of past guide at most, often none, while their archive plays two or
/// three days back. Nothing upstream hands the older titles over after the fact, so the only way to
/// have them is to keep what the plugin already saw. Everything recorded here comes from guide
/// data that was fetched anyway, for the XMLTV output or for a catch-up browse. This class never
/// asks the provider for anything.
/// </para>
/// <para>
/// One JSON file per channel under the plugin's data folder. Flat files because the release ships
/// as a single DLL, and one per channel so a browse reads only the channel it shows.
/// </para>
/// </summary>
public sealed class CatchupHistoryStore : IDisposable
{
    /// <summary>
    /// Version written into every file. A file carrying another one is ignored and rewritten.
    /// </summary>
    internal const int FileFormatVersion = 1;

    /// <summary>
    /// A file nobody has written to for this long belongs to a channel that is gone. Longer than
    /// any archive a provider keeps, so a quiet channel is never swept while its entries still
    /// count.
    /// </summary>
    internal static readonly TimeSpan StaleFileAge = TimeSpan.FromDays(32);

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _root;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="CatchupHistoryStore"/> class.
    /// </summary>
    /// <param name="root">Folder holding one file per channel. Created on first write.</param>
    /// <param name="logger">Logger.</param>
    public CatchupHistoryStore(string root, ILogger logger)
    {
        _root = root;
        _logger = logger;
    }

    /// <summary>
    /// The file holding one channel's history.
    /// <para>
    /// Keyed on the provider's fingerprint, not its position in the list. Stream ids overlap
    /// between providers, so after a reorder or a replaced provider a positional key would show
    /// one provider's titles on another provider's channel. A provider that changes gets new files;
    /// the old ones age out through the stale-file sweep.
    /// </para>
    /// </summary>
    /// <param name="providerKey">The provider's fingerprint, see <see cref="LiveTvService.BuildProviderFingerprints"/>.</param>
    /// <param name="streamId">Xtream live stream id.</param>
    /// <returns>The file name, without folder.</returns>
    internal static string FileName(string providerKey, int streamId)
        => string.Create(CultureInfo.InvariantCulture, $"{providerKey}_{streamId}.json");

    /// <summary>
    /// Merges a fresh pull into what is stored for one channel.
    /// <para>
    /// The same rule Dispatcharr uses for its own guide retention. The earliest start in the pull
    /// marks where the provider's current view begins. A stored programme that ended at or before
    /// that point is history the pull no longer covers and is kept. One that ends after it runs
    /// into the new data or sits after it, and the pull replaces it, so a schedule change is picked
    /// up rather than listed twice.
    /// </para>
    /// <para>
    /// Upcoming programmes are recorded as well. A provider that publishes only what is coming
    /// would otherwise never leave anything behind: each programme drops out of its feed the moment
    /// it airs.
    /// </para>
    /// <para>
    /// An empty pull leaves the stored guide alone apart from the horizon. An empty answer is far
    /// more likely a hiccup than a channel whose past changed.
    /// </para>
    /// </summary>
    /// <param name="stored">What is on disk for the channel, oldest first.</param>
    /// <param name="incoming">Programmes from the pull just made.</param>
    /// <param name="horizonUnix">Oldest instant still worth keeping, UTC unix seconds.</param>
    /// <returns>The merged guide, oldest first.</returns>
    internal static List<CatchupHistoryEntry> Merge(
        IEnumerable<CatchupHistoryEntry> stored,
        IEnumerable<CatchupHistoryEntry> incoming,
        long horizonUnix)
    {
        // Starts before the horizon don't count for firstStart, so one bad early timestamp in the
        // pull can't reach back and delete retained history.
        var fresh = incoming
            .Where(p => p.Stop > p.Start && p.Stop > horizonUnix)
            .ToList();
        var inWindow = fresh.Where(p => p.Start >= horizonUnix).ToList();
        long? firstStart = inWindow.Count == 0 ? null : inWindow.Min(p => p.Start);

        var kept = stored
            .Where(p => p.Stop > p.Start && p.Stop > horizonUnix)
            .Where(p => firstStart == null || p.Stop <= firstStart.Value);

        // Keyed on start, the pull winning: a programme spanning the horizon is both kept and in
        // the pull, and must not appear twice.
        var merged = new SortedDictionary<long, CatchupHistoryEntry>();
        foreach (var entry in kept)
        {
            merged[entry.Start] = entry;
        }

        foreach (var entry in fresh)
        {
            merged[entry.Start] = entry;
        }

        return merged.Values.ToList();
    }

    /// <summary>
    /// Reads one channel's history.
    /// </summary>
    /// <param name="providerKey">The provider's fingerprint.</param>
    /// <param name="streamId">Xtream live stream id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored programmes, oldest first; empty when there is no file or it is unreadable.</returns>
    public async Task<IReadOnlyList<CatchupHistoryEntry>> LoadAsync(string providerKey, int streamId, CancellationToken cancellationToken)
    {
        // No lock: writes go to a temporary file that is moved into place, so a reader sees either
        // the previous file or the next one, never half of one. On Windows a read in progress can
        // make that move fail instead; the write is then skipped and made on the next pull.
        return await ReadFileAsync(Path.Combine(_root, FileName(providerKey, streamId)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records pulls for any number of channels.
    /// </summary>
    /// <param name="pulls">One entry per channel. A channel may appear only once.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="sweepStaleFiles">True for a pull covering every channel, which is the only moment
    /// a file that nobody writes to any more can be told apart from a quiet channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RecordAsync(
        IReadOnlyCollection<CatchupHistoryPull> pulls,
        DateTimeOffset nowUtc,
        bool sweepStaleFiles,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            int written = 0;
            foreach (var pull in pulls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((await RecordOneAsync(pull, nowUtc, cancellationToken).ConfigureAwait(false)).Written)
                {
                    written++;
                }
            }

            int swept = sweepStaleFiles ? SweepStaleFiles(nowUtc) : 0;
            _logger.LogDebug(
                "Catch-up history: {Channels} channels seen, {Written} files updated, {Swept} stale files removed",
                pulls.Count,
                written,
                swept);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Records one channel's pull and returns its guide, recorded history and the pull together.
    /// <para>
    /// Returns the merge itself rather than reading the file back, so a write that fails still
    /// shows the pull that was just fetched.
    /// </para>
    /// </summary>
    /// <param name="pull">The channel and its programmes.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The channel's guide, oldest first.</returns>
    public async Task<IReadOnlyList<CatchupHistoryEntry>> RecordAndLoadAsync(
        CatchupHistoryPull pull,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            return (await RecordOneAsync(pull, nowUtc, cancellationToken).ConfigureAwait(false)).Merged;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Converts a programme as the provider's JSON EPG sends it, title and description base64.
    /// </summary>
    /// <param name="programme">The programme.</param>
    /// <returns>The entry, in plain text.</returns>
    internal static CatchupHistoryEntry FromEpg(EpgProgram programme)
        => new(
            programme.StartTimestamp,
            programme.StopTimestamp,
            LiveTvService.DecodeBase64(programme.Title),
            LiveTvService.DecodeBase64(programme.Description));

    /// <summary>
    /// Converts an entry back into the shape the catch-up browser reads.
    /// <para>
    /// Title and description go back to base64, since that browser decodes them the way the
    /// provider sends them. Passing plain text instead would work for most titles and garble the
    /// ones that happen to be valid base64, "News" for one.
    /// </para>
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The programme.</returns>
    internal static EpgProgram ToEpg(CatchupHistoryEntry entry) => new()
    {
        StartTimestamp = entry.Start,
        StopTimestamp = entry.Stop,
        Title = Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Title)),
        Description = Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Description)),
    };

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    private async Task<(bool Written, List<CatchupHistoryEntry> Merged)> RecordOneAsync(CatchupHistoryPull pull, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, FileName(pull.ProviderKey, pull.StreamId));
        var stored = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        long horizon = CatchupPlanner.ArchiveHorizonUtc(pull.ArchiveDays, TimeZoneInfo.Local, nowUtc).ToUnixTimeSeconds();
        var merged = Merge(stored, pull.Programmes, horizon);

        if (merged.SequenceEqual(stored))
        {
            return (false, merged);
        }

        var tmpPath = path + ".tmp";
        try
        {
            if (merged.Count == 0)
            {
                File.Delete(path);
            }
            else
            {
                var json = JsonSerializer.Serialize(new CatchupHistoryFile { Version = FileFormatVersion, Programmes = merged }, JsonOptions);
                await File.WriteAllTextAsync(tmpPath, json, cancellationToken).ConfigureAwait(false);
                File.Move(tmpPath, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // One file that can't be written costs that channel this pull, not every channel
            // after it in a batch, and not the programmes a browse just fetched: the merge is
            // returned either way and the next pull writes it.
            _logger.LogWarning(ex, "Could not record catch-up history for provider {Provider} stream {StreamId}", pull.ProviderKey, pull.StreamId);
            TryDelete(tmpPath);
            return (false, merged);
        }

        return (true, merged);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the stale-file sweep.
        }
    }

    private async Task<IReadOnlyList<CatchupHistoryEntry>> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<CatchupHistoryEntry>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<CatchupHistoryFile>(json, JsonOptions);
            if (file?.Programmes == null || file.Version != FileFormatVersion)
            {
                return Array.Empty<CatchupHistoryEntry>();
            }

            return file.Programmes.OrderBy(p => p.Start).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file costs that channel its titles, which the blocks cover. It must not
            // cost the browse: the next write replaces it.
            _logger.LogWarning(ex, "Ignoring unreadable catch-up history file {Path}", path);
            return Array.Empty<CatchupHistoryEntry>();
        }
    }

    private int SweepStaleFiles(DateTimeOffset nowUtc)
    {
        int removed = 0;
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles())
        {
            // A temporary file outlives its write only when the process died in between; an hour
            // is far longer than any write takes.
            var maxAge = file.Name.EndsWith(".tmp", StringComparison.Ordinal) ? TimeSpan.FromHours(1) : StaleFileAge;
            if ((file.Name.EndsWith(".json", StringComparison.Ordinal) || file.Name.EndsWith(".tmp", StringComparison.Ordinal))
                && nowUtc.UtcDateTime - file.LastWriteTimeUtc > maxAge)
            {
                file.Delete();
                removed++;
            }
        }

        return removed;
    }
}

/// <summary>
/// One programme in a channel's recorded guide. Title and description are plain text.
/// </summary>
/// <param name="Start">Start, UTC unix seconds.</param>
/// <param name="Stop">Stop, UTC unix seconds.</param>
/// <param name="Title">Title.</param>
/// <param name="Description">Description, empty when the guide had none.</param>
public sealed record CatchupHistoryEntry(long Start, long Stop, string Title, string Description);

/// <summary>
/// What one pull said about one channel.
/// </summary>
/// <param name="ProviderKey">The provider's fingerprint, see <see cref="LiveTvService.BuildProviderFingerprints"/>.</param>
/// <param name="StreamId">Xtream live stream id.</param>
/// <param name="ArchiveDays">Days of archive the channel can be browsed for, see <see cref="CatchupPlanner.DayCount"/>.</param>
/// <param name="Programmes">The programmes in the pull.</param>
public sealed record CatchupHistoryPull(string ProviderKey, int StreamId, int ArchiveDays, IReadOnlyList<CatchupHistoryEntry> Programmes);

/// <summary>
/// On-disk shape of one channel's file.
/// </summary>
internal sealed class CatchupHistoryFile
{
    /// <summary>Gets or sets the format version; a file of another version is ignored and rewritten.</summary>
    public int Version { get; set; }

    /// <summary>Gets or sets the programmes, oldest first.</summary>
    public List<CatchupHistoryEntry> Programmes { get; set; } = new();
}
