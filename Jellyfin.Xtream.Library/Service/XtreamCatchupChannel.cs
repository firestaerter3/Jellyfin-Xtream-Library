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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Browses programmes that have already aired, from the provider's archive (GitHub #108).
/// <para>
/// Jellyfin's own Live TV cannot play a finished programme: the web client refuses any
/// <c>Program</c> item whose end time has passed, with no setting and no extension point to
/// override it. Items surfaced through <see cref="IChannel"/> are not <c>Program</c> items, so
/// they never meet that check. That is the whole reason this exists as a channel rather than as an
/// addition to the Live TV guide.
/// </para>
/// <para>
/// Three levels: channels, then days, then programmes. Only the last one costs a request, and only
/// for the channel being opened.
/// </para>
/// </summary>
public class XtreamCatchupChannel : IChannel, IDisableMediaSourceDisplay, IRequiresMediaInfoCallback, IHasCacheKey
{
    private readonly LiveTvService _liveTvService;
    private readonly CatchupEpgCache _epgCache;
    private readonly ILogger<XtreamCatchupChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XtreamCatchupChannel"/> class.
    /// </summary>
    /// <param name="liveTvService">The Live TV service, for the channel list.</param>
    /// <param name="epgCache">The catch-up EPG cache.</param>
    /// <param name="logger">Logger.</param>
    public XtreamCatchupChannel(
        LiveTvService liveTvService,
        CatchupEpgCache epgCache,
        ILogger<XtreamCatchupChannel> logger)
    {
        _liveTvService = liveTvService;
        _epgCache = epgCache;
        _logger = logger;
    }

    /// <summary>
    /// Gets the channel name.
    /// <para>
    /// Do not change this. Jellyfin hashes it into the library id of the channel, so renaming it
    /// orphans every folder and programme underneath along with their watched state, and leaves a
    /// dead row behind.
    /// </para>
    /// </summary>
    public string Name => "Xtream Catch-up";

    /// <inheritdoc />
    public string Description => "Watch programmes that have already aired, from your provider's archive.";

    /// <summary>
    /// Gets the item-shape version. Bump only when the shape of the items below changes, because
    /// Jellyfin uses this as a directory name for its own cache and never cleans the old ones.
    /// Day-to-day freshness is <see cref="GetCacheKey"/>'s job instead.
    /// </summary>
    public string DataVersion => "1";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.TvExtra },
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
    };

    /// <summary>
    /// Whether the channel shows at all. Ignores the user: what is in the archive does not vary by
    /// who is looking.
    /// <para>
    /// Gating here rather than by skipping the registration is deliberate. An unregistered channel
    /// gets deleted from the library on the next scan, taking watched state with it; one that
    /// simply reports itself disabled comes back intact when the setting is turned on again.
    /// </para>
    /// </summary>
    /// <param name="userId">Ignored.</param>
    /// <returns>True when catch-up browsing is switched on and a provider is configured.</returns>
    public bool IsEnabledFor(string userId)
    {
        var config = Plugin.Instance.Configuration;
        return config.EnableLiveTv
               && config.EnableCatchup
               && config.ShowCatchupInJellyfin
               && LiveTvService.ResolveLiveTvProviders(config).Any();
    }

    /// <summary>
    /// Gets the key Jellyfin files its cached listings under.
    /// <para>
    /// The day list rolls at midnight, so the key carries the date. This rather than a dated
    /// <see cref="DataVersion"/>, which would make a fresh cache directory every day and leave
    /// every previous one behind forever.
    /// </para>
    /// </summary>
    /// <param name="userId">Ignored; catch-up content is the same for everyone.</param>
    /// <returns>The cache key. Never null, since Jellyfin concatenates it unguarded.</returns>
    public string GetCacheKey(string? userId)
    {
        var config = Plugin.Instance.Configuration;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyyMMdd}-{config.CatchupDays}");
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    /// <summary>
    /// Lists whatever sits under the given folder.
    /// <para>
    /// Jellyfin never populates <c>StartIndex</c> or <c>Limit</c> here; it pages the library
    /// database afterwards. So this always returns the whole level, and on a large provider the
    /// channel list legitimately runs to thousands of folders.
    /// </para>
    /// </summary>
    /// <param name="query">What to list. An empty folder id means the root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The items at that level.</returns>
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrEmpty(query.FolderId))
        {
            return await GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!CatchupItemId.TryParse(query.FolderId, out var reference))
        {
            // An id from an older build of the plugin. An empty folder is the right answer; an
            // exception here reaches the user as a broken screen.
            _logger.LogWarning("Ignoring unrecognised catch-up folder id {FolderId}", query.FolderId);
            return Empty();
        }

        return reference.Kind switch
        {
            CatchupItemKind.Channel => await GetDaysAsync(reference, cancellationToken).ConfigureAwait(false),
            CatchupItemKind.Day => await GetProgrammesAsync(reference, cancellationToken).ConfigureAwait(false),
            _ => Empty(),
        };
    }

    /// <summary>
    /// Builds the stream for one programme, at the moment it is played.
    /// <para>
    /// Built here rather than stored on the item because the URL carries the provider password.
    /// Putting it on the item would write it into Jellyfin's own cache files and library database,
    /// where it would also go stale after a password change or a clock correction. The programme id
    /// already carries the time window, so this costs no request.
    /// </para>
    /// </summary>
    /// <param name="id">The programme's item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One media source, or none when the id is not a programme.</returns>
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        if (!CatchupItemId.TryParse(id, out var reference) || reference.Kind != CatchupItemKind.Programme)
        {
            _logger.LogWarning("Asked for a catch-up stream for {Id}, which is not a programme", id);
            return Array.Empty<MediaSourceInfo>();
        }

        var config = Plugin.Instance.Configuration;
        var startUtc = DateTimeOffset.FromUnixTimeSeconds(reference.StartUnix);
        var clock = await _epgCache.GetClockAsync(
            reference.ProviderIndex,
            startUtc,
            config.CatchupTimeShiftMinutes,
            cancellationToken).ConfigureAwait(false);

        var (baseUrl, username, password) = LiveTvService.ResolveLiveTvProvider(config, reference.ProviderIndex);
        int minutes = CatchupPlanner.DurationMinutes(reference.StartUnix, reference.StopUnix);
        var url = CatchupUrlBuilder.BuildPlaybackUrl(
            baseUrl, username, password, reference.StreamId, startUtc, minutes, clock.Offset);

        return new[] { BuildMediaSource(id, url, reference) };
    }

    /// <summary>
    /// The media source for one finished programme.
    /// <para>
    /// Deliberately not the live one from the tuner. A recording is finite, so it is not an
    /// infinite stream and it has a runtime, without which a client cannot offer a seek bar. No
    /// placeholder streams either: those exist on the live path to skip probing, and a finite file
    /// is cheap to probe properly.
    /// </para>
    /// </summary>
    /// <param name="id">The programme's item id, reused as the source id.</param>
    /// <param name="url">The resolved timeshift URL.</param>
    /// <param name="reference">The decoded programme reference.</param>
    /// <returns>The media source.</returns>
    internal static MediaSourceInfo BuildMediaSource(string id, string url, CatchupItemRef reference) => new()
    {
        Id = id,
        Path = url,
        Protocol = MediaProtocol.Http,
        Container = "mpegts",
        IsRemote = true,
        IsInfiniteStream = false,
        RunTimeTicks = (reference.StopUnix - reference.StartUnix) * TimeSpan.TicksPerSecond,
        SupportsProbing = true,
        SupportsDirectPlay = false,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
    };

    private static ChannelItemInfo BuildItem(
        CatchupItemRef reference,
        DateTimeOffset startUtc,
        DateTimeOffset stopUtc,
        string name,
        string? overview) => new()
    {
        Id = CatchupItemId.ForProgramme(
            reference.ProviderIndex,
            reference.StreamId,
            startUtc.ToUnixTimeSeconds(),
            stopUtc.ToUnixTimeSeconds()),
        Name = name,
        Overview = overview,
        Type = ChannelItemType.Media,
        ContentType = ChannelMediaContentType.TvExtra,
        MediaType = ChannelMediaType.Video,

        // Both of these matter. A true IsLiveStream makes Jellyfin discard RunTimeTicks, and
        // without a runtime a client cannot draw a seek bar for something that has finished.
        IsLiveStream = false,
        RunTimeTicks = (stopUtc - startUtc).Ticks,
        PremiereDate = startUtc.UtcDateTime,
        DateCreated = startUtc.UtcDateTime,
    };

    private static ChannelItemResult Empty()
        => new() { Items = Array.Empty<ChannelItemInfo>(), TotalRecordCount = 0 };

    private async Task<ChannelItemResult> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var channels = await _liveTvService.GetCatchupCapableChannelsAsync(cancellationToken).ConfigureAwait(false);
        var config = Plugin.Instance.Configuration;

        var items = channels.Select(channel => new ChannelItemInfo
        {
            Id = CatchupItemId.ForChannel(channel.ProviderIndex, channel.StreamId),
            Name = ChannelNameCleaner.CleanChannelName(
                channel.Name,
                config.ChannelRemoveTerms,
                config.EnableChannelNameCleaning),
            ImageUrl = channel.StreamIcon,
            Type = ChannelItemType.Folder,
        }).ToList();

        _logger.LogDebug("Catch-up root listed {Count} channels", items.Count);
        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<ChannelItemResult> GetDaysAsync(CatchupItemRef reference, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var channels = await _liveTvService.GetCatchupCapableChannelsAsync(cancellationToken).ConfigureAwait(false);
        var channel = channels.FirstOrDefault(c =>
            c.ProviderIndex == reference.ProviderIndex && c.StreamId == reference.StreamId);

        if (channel == null)
        {
            _logger.LogInformation(
                "Catch-up channel {StreamId} on provider {Provider} is no longer offered by the provider",
                reference.StreamId,
                reference.ProviderIndex);
            return Empty();
        }

        int days = CatchupPlanner.DayCount(config.CatchupDays, channel.TvArchiveDuration);
        var zone = TimeZoneInfo.Local;
        var now = DateTimeOffset.UtcNow;

        var items = new List<ChannelItemInfo>(days);
        for (int daysAgo = 0; daysAgo < days; daysAgo++)
        {
            var (from, _) = CatchupPlanner.DayWindowUtc(daysAgo, zone, now);
            var label = TimeZoneInfo.ConvertTime(from, zone).ToString("ddd d MMM", CultureInfo.CurrentCulture);

            items.Add(new ChannelItemInfo
            {
                Id = CatchupItemId.ForDay(reference.ProviderIndex, reference.StreamId, daysAgo),
                Name = daysAgo == 0 ? $"Today, {label}" : label,
                Type = ChannelItemType.Folder,
            });
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<ChannelItemResult> GetProgrammesAsync(CatchupItemRef reference, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var channels = await _liveTvService.GetCatchupCapableChannelsAsync(cancellationToken).ConfigureAwait(false);
        var channel = channels.FirstOrDefault(c =>
            c.ProviderIndex == reference.ProviderIndex && c.StreamId == reference.StreamId);

        if (channel == null)
        {
            return Empty();
        }

        var fresh = await _epgCache.GetProgrammesAsync(
            reference.ProviderIndex,
            reference.StreamId,
            config.EpgCacheMinutes,
            cancellationToken).ConfigureAwait(false);

        var zone = TimeZoneInfo.Local;
        var now = DateTimeOffset.UtcNow;
        int days = CatchupPlanner.DayCount(config.CatchupDays, channel.TvArchiveDuration);
        var programmes = await WithHistoryAsync(reference, days, fresh, now, cancellationToken).ConfigureAwait(false);

        var (from, to) = CatchupPlanner.DayWindowUtc(reference.DaysAgo, zone, now);
        var horizon = CatchupPlanner.ArchiveHorizonUtc(days, zone, now);
        var selected = CatchupPlanner.ProgrammesInWindow(programmes, from, to, now, horizon);

        var entries = selected.Select(programme =>
        {
            var startUtc = DateTimeOffset.FromUnixTimeSeconds(programme.StartTimestamp);
            var stopUtc = DateTimeOffset.FromUnixTimeSeconds(programme.StopTimestamp);
            var localStart = TimeZoneInfo.ConvertTime(startUtc, zone);
            var title = LiveTvService.DecodeBase64(programme.Title);

            return (Start: startUtc, Item: BuildItem(
                reference,
                startUtc,
                stopUtc,
                $"{localStart:HH:mm} {title}",
                LiveTvService.DecodeBase64(programme.Description)));
        }).ToList();

        int blockCount = 0;
        if (config.CatchupBlockMinutes > 0)
        {
            // Providers publish little or no past guide, so parts of a day, or all of it, have
            // nothing to list even though the archive plays. Those parts are offered in blocks:
            // every stream URL is built from a time window anyway, so a block is as playable as a
            // programme. Only what is listed counts as covered: a programme still airing, or one
            // that began before the horizon, is not listed, and its finished part must stay
            // reachable as blocks.
            var blocks = CatchupPlanner.GapBlocks(selected, from, to, now, horizon, config.CatchupBlockMinutes);
            blockCount = blocks.Count;
            entries.AddRange(blocks.Select(block => (Start: block.FromUtc, Item: BuildItem(
                reference,
                block.FromUtc,
                block.ToUtc,
                TimeZoneInfo.ConvertTime(block.FromUtc, zone).ToString("HH:mm", CultureInfo.CurrentCulture)
                    + " - "
                    + TimeZoneInfo.ConvertTime(block.ToUtc, zone).ToString("HH:mm", CultureInfo.CurrentCulture),
                null))));
        }

        var items = entries.OrderBy(e => e.Start).Select(e => e.Item).ToList();

        _logger.LogDebug(
            "Catch-up channel {StreamId} day -{DaysAgo}: {Count} programmes of {Total} known, {Blocks} blocks of {Minutes} min for the gaps",
            reference.StreamId,
            reference.DaysAgo,
            selected.Count,
            programmes.Count,
            blockCount,
            config.CatchupBlockMinutes);

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <summary>
    /// Records the channel's fresh guide and returns it together with what was recorded before.
    /// <para>
    /// The fresh guide is what was fetched for this browse anyway, so recording it costs no
    /// request. A failure here costs the recorded titles, never the browse: the fresh guide is
    /// still returned.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<EpgProgram>> WithHistoryAsync(
        CatchupItemRef reference,
        int days,
        IReadOnlyList<EpgProgram> fresh,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!LiveTvService.BuildProviderFingerprints(Plugin.Instance.Configuration).TryGetValue(reference.ProviderIndex, out var providerKey))
            {
                return fresh;
            }

            var pull = new CatchupHistoryPull(
                providerKey,
                reference.StreamId,
                days,
                fresh.Select(CatchupHistoryStore.FromEpg).ToList());
            var merged = await _liveTvService.CatchupHistory.RecordAndLoadAsync(pull, now, cancellationToken).ConfigureAwait(false);
            return merged.Select(CatchupHistoryStore.ToEpg).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catch-up history unavailable for channel {StreamId}; showing the provider's guide only", reference.StreamId);
            return fresh;
        }
    }
}
