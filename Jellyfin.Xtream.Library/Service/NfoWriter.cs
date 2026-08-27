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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Writes Kodi-style NFO sidecar files with media stream information.
/// </summary>
public static class NfoWriter
{
    /// <summary>
    /// Writes a movie NFO file with provider identifiers and/or stream details.
    /// </summary>
    /// <param name="nfoPath">Path to the NFO file.</param>
    /// <param name="title">Movie title.</param>
    /// <param name="video">Video stream info.</param>
    /// <param name="audio">Audio stream info.</param>
    /// <param name="durationSecs">Duration in seconds.</param>
    /// <param name="tmdbId">Optional TMDb ID for provider identification.</param>
    /// <param name="year">Optional release year.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="plot">Optional plot/description.</param>
    /// <param name="genre">Optional comma-separated genre list.</param>
    /// <param name="director">Optional director name.</param>
    /// <param name="cast">Optional comma-separated cast list.</param>
    /// <param name="country">Optional production country.</param>
    /// <param name="rating">Optional rating (e.g. "7.2"). Ignored if not a valid number.</param>
    /// <param name="premiered">Optional release date in yyyy-MM-dd format. Ignored if not in that format.</param>
    /// <param name="youtubeTrailerId">Optional YouTube video id for the trailer.</param>
    /// <param name="dateAdded">Optional date the content was added on the provider's catalog.</param>
    /// <param name="posterUrl">Optional remote URL to the poster/cover image. Referenced directly, never downloaded.</param>
    /// <param name="backdropUrl">Optional remote URL to the backdrop/fanart image. Referenced directly, never downloaded.</param>
    /// <returns>True if NFO was written, false if no data was available.</returns>
    public static async Task<bool> WriteMovieNfoAsync(
        string nfoPath,
        string title,
        VideoInfo? video,
        AudioInfo? audio,
        int? durationSecs,
        int? tmdbId,
        int? year,
        CancellationToken cancellationToken,
        string? plot = null,
        string? genre = null,
        string? director = null,
        string? cast = null,
        string? country = null,
        string? rating = null,
        string? premiered = null,
        string? youtubeTrailerId = null,
        DateTime? dateAdded = null,
        string? posterUrl = null,
        string? backdropUrl = null)
    {
        bool hasMedia = HasUsableData(video, audio);
        bool hasExtendedMetadata = HasUsableExtendedMetadata(plot, genre, director, cast, country, rating, premiered, youtubeTrailerId, dateAdded) ||
            !string.IsNullOrWhiteSpace(posterUrl) || !string.IsNullOrWhiteSpace(backdropUrl);

        // Skip if no provider ID, no media info and no other usable metadata
        if (!tmdbId.HasValue && !hasMedia && !hasExtendedMetadata)
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<movie>");
        sb.Append("  <title>").Append(EscapeXml(title)).AppendLine("</title>");

        if (year.HasValue)
        {
            sb.Append("  <year>").Append(year.Value.ToString(CultureInfo.InvariantCulture)).AppendLine("</year>");
        }

        AppendPlot(sb, plot);
        AppendRating(sb, rating);
        AppendPremiered(sb, premiered);
        AppendGenres(sb, genre);
        AppendCountry(sb, country);
        AppendDirector(sb, director);
        AppendCast(sb, cast);
        AppendThumb(sb, posterUrl);
        AppendFanart(sb, backdropUrl);
        AppendTrailer(sb, youtubeTrailerId);

        if (tmdbId.HasValue)
        {
            sb.Append("  <uniqueid type=\"tmdb\" default=\"true\">").Append(tmdbId.Value.ToString(CultureInfo.InvariantCulture)).AppendLine("</uniqueid>");
        }

        AppendDateAdded(sb, dateAdded);

        if (hasMedia)
        {
            AppendFileInfo(sb, video, audio, durationSecs);
        }

        sb.AppendLine("</movie>");

        await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Writes a tvshow NFO file with provider identifiers for series-level identification.
    /// </summary>
    /// <param name="nfoPath">Path to the tvshow.nfo file.</param>
    /// <param name="title">Series title.</param>
    /// <param name="tmdbId">Optional TMDb ID.</param>
    /// <param name="tvdbId">Optional TVDb ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="plot">Optional plot/description.</param>
    /// <param name="genre">Optional comma-separated genre list.</param>
    /// <param name="director">Optional director name.</param>
    /// <param name="cast">Optional comma-separated cast list.</param>
    /// <param name="rating">Optional rating (0-10 scale).</param>
    /// <param name="posterUrl">Optional remote URL to the poster/cover image. Referenced directly, never downloaded.</param>
    /// <param name="backdropUrl">Optional remote URL to the backdrop/fanart image. Referenced directly, never downloaded.</param>
    /// <returns>True if NFO was written, false if no provider IDs or other usable metadata were available.</returns>
    public static async Task<bool> WriteShowNfoAsync(
        string nfoPath,
        string title,
        int? tmdbId,
        int? tvdbId,
        CancellationToken cancellationToken,
        string? plot = null,
        string? genre = null,
        string? director = null,
        string? cast = null,
        decimal? rating = null,
        string? posterUrl = null,
        string? backdropUrl = null)
    {
        bool hasExtendedMetadata = HasUsableExtendedMetadata(plot, genre, director, cast, null, null, null, null, null) ||
            IsUsableRating(rating) || !string.IsNullOrWhiteSpace(posterUrl) || !string.IsNullOrWhiteSpace(backdropUrl);

        if (!tmdbId.HasValue && !tvdbId.HasValue && !hasExtendedMetadata)
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<tvshow>");
        sb.Append("  <title>").Append(EscapeXml(title)).AppendLine("</title>");

        AppendPlot(sb, plot);
        AppendRating(sb, rating);
        AppendGenres(sb, genre);
        AppendDirector(sb, director);
        AppendCast(sb, cast);
        AppendThumb(sb, posterUrl);
        AppendFanart(sb, backdropUrl);

        // TVDb is the primary identifier for series; TMDb is secondary
        if (tvdbId.HasValue)
        {
            sb.Append("  <uniqueid type=\"tvdb\" default=\"true\">").Append(tvdbId.Value.ToString(CultureInfo.InvariantCulture)).AppendLine("</uniqueid>");
        }

        if (tmdbId.HasValue)
        {
            string defaultAttr = tvdbId.HasValue ? string.Empty : " default=\"true\"";
            sb.Append(CultureInfo.InvariantCulture, $"  <uniqueid type=\"tmdb\"{defaultAttr}>").Append(tmdbId.Value.ToString(CultureInfo.InvariantCulture)).AppendLine("</uniqueid>");
        }

        sb.AppendLine("</tvshow>");

        await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Writes an episode NFO file with stream details only (no title/identification).
    /// Title is omitted so Jellyfin's metadata providers (TMDb/TVDb) supply the
    /// proper episode name instead of the raw Xtream filename.
    /// </summary>
    /// <param name="nfoPath">Path to the NFO file.</param>
    /// <param name="video">Video stream info.</param>
    /// <param name="audio">Audio stream info.</param>
    /// <param name="durationSecs">Duration in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="plot">Optional episode plot/description.</param>
    /// <param name="premiered">Optional air date in yyyy-MM-dd format. Ignored if not in that format.</param>
    /// <param name="rating">Optional rating (0-10 scale).</param>
    /// <param name="dateAdded">Optional date the episode was added on the provider's catalog.</param>
    /// <param name="thumbUrl">Optional remote URL to the episode screenshot. Referenced directly, never downloaded.</param>
    /// <returns>True if NFO was written, false if no media info or other usable metadata was available.</returns>
    public static async Task<bool> WriteEpisodeNfoAsync(
        string nfoPath,
        VideoInfo? video,
        AudioInfo? audio,
        int? durationSecs,
        CancellationToken cancellationToken,
        string? plot = null,
        string? premiered = null,
        decimal? rating = null,
        DateTime? dateAdded = null,
        string? thumbUrl = null)
    {
        bool hasExtendedMetadata = HasUsableExtendedMetadata(plot, null, null, null, null, null, premiered, null, dateAdded) ||
            IsUsableRating(rating) || !string.IsNullOrWhiteSpace(thumbUrl);

        if (!HasUsableData(video, audio) && !hasExtendedMetadata)
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<episodedetails>");

        AppendPlot(sb, plot);
        AppendRating(sb, rating);
        AppendPremiered(sb, premiered);
        AppendThumb(sb, thumbUrl);
        AppendDateAdded(sb, dateAdded);

        if (HasUsableData(video, audio))
        {
            AppendFileInfo(sb, video, audio, durationSecs);
        }

        sb.AppendLine("</episodedetails>");

        await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void AppendFileInfo(StringBuilder sb, VideoInfo? video, AudioInfo? audio, int? durationSecs)
    {
        sb.AppendLine("  <fileinfo>");
        sb.AppendLine("    <streamdetails>");

        if (video != null)
        {
            sb.AppendLine("      <video>");

            if (!string.IsNullOrEmpty(video.CodecName))
            {
                sb.Append("        <codec>").Append(EscapeXml(video.CodecName)).AppendLine("</codec>");
            }

            if (video.Width > 0)
            {
                sb.Append("        <width>").Append(video.Width.ToString(CultureInfo.InvariantCulture)).AppendLine("</width>");
            }

            if (video.Height > 0)
            {
                sb.Append("        <height>").Append(video.Height.ToString(CultureInfo.InvariantCulture)).AppendLine("</height>");
            }

            if (!string.IsNullOrEmpty(video.AspectRatio))
            {
                // Convert "16:9" to decimal aspect ratio
                var aspectDecimal = ParseAspectRatio(video.AspectRatio);
                if (aspectDecimal.HasValue)
                {
                    sb.Append("        <aspect>").Append(aspectDecimal.Value.ToString("F2", CultureInfo.InvariantCulture)).AppendLine("</aspect>");
                }
            }

            if (durationSecs.HasValue && durationSecs.Value > 0)
            {
                sb.Append("        <durationinseconds>").Append(durationSecs.Value.ToString(CultureInfo.InvariantCulture)).AppendLine("</durationinseconds>");
            }

            sb.AppendLine("      </video>");
        }

        if (audio != null)
        {
            sb.AppendLine("      <audio>");

            if (!string.IsNullOrEmpty(audio.CodecName))
            {
                sb.Append("        <codec>").Append(EscapeXml(audio.CodecName)).AppendLine("</codec>");
            }

            if (audio.Channels > 0)
            {
                sb.Append("        <channels>").Append(audio.Channels.ToString(CultureInfo.InvariantCulture)).AppendLine("</channels>");
            }

            sb.AppendLine("      </audio>");
        }

        sb.AppendLine("    </streamdetails>");
        sb.AppendLine("  </fileinfo>");
    }

    private static void AppendPlot(StringBuilder sb, string? plot)
    {
        if (!string.IsNullOrWhiteSpace(plot))
        {
            sb.Append("  <plot>").Append(EscapeXml(plot)).AppendLine("</plot>");
        }
    }

    private static void AppendGenres(StringBuilder sb, string? genre)
    {
        foreach (var value in SplitCsv(genre))
        {
            sb.Append("  <genre>").Append(EscapeXml(value)).AppendLine("</genre>");
        }
    }

    private static void AppendCast(StringBuilder sb, string? cast)
    {
        foreach (var actor in SplitCsv(cast))
        {
            sb.AppendLine("  <actor>");
            sb.Append("    <name>").Append(EscapeXml(actor)).AppendLine("</name>");
            sb.AppendLine("  </actor>");
        }
    }

    private static void AppendDirector(StringBuilder sb, string? director)
    {
        if (!string.IsNullOrWhiteSpace(director))
        {
            sb.Append("  <director>").Append(EscapeXml(director)).AppendLine("</director>");
        }
    }

    private static void AppendCountry(StringBuilder sb, string? country)
    {
        if (!string.IsNullOrWhiteSpace(country))
        {
            sb.Append("  <country>").Append(EscapeXml(country)).AppendLine("</country>");
        }
    }

    private static void AppendThumb(StringBuilder sb, string? posterUrl)
    {
        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            sb.Append("  <thumb aspect=\"poster\">").Append(EscapeXml(posterUrl)).AppendLine("</thumb>");
        }
    }

    private static void AppendFanart(StringBuilder sb, string? backdropUrl)
    {
        if (!string.IsNullOrWhiteSpace(backdropUrl))
        {
            sb.AppendLine("  <fanart>");
            sb.Append("    <thumb>").Append(EscapeXml(backdropUrl)).AppendLine("</thumb>");
            sb.AppendLine("  </fanart>");
        }
    }

    private static void AppendTrailer(StringBuilder sb, string? youtubeTrailerId)
    {
        if (string.IsNullOrWhiteSpace(youtubeTrailerId))
        {
            return;
        }

        var url = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(youtubeTrailerId);
        sb.Append("  <trailer>").Append(EscapeXml(url)).AppendLine("</trailer>");
    }

    private static void AppendDateAdded(StringBuilder sb, DateTime? dateAdded)
    {
        if (dateAdded.HasValue)
        {
            sb.Append("  <dateadded>").Append(dateAdded.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).AppendLine("</dateadded>");
        }
    }

    private static void AppendPremiered(StringBuilder sb, string? premiered)
    {
        if (IsUsablePremiered(premiered))
        {
            sb.Append("  <premiered>").Append(premiered).AppendLine("</premiered>");
        }
    }

    private static void AppendRating(StringBuilder sb, string? rating)
    {
        if (decimal.TryParse(rating, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            AppendRating(sb, (decimal?)parsed);
        }
    }

    private static void AppendRating(StringBuilder sb, decimal? rating)
    {
        if (IsUsableRating(rating))
        {
            sb.Append("  <rating>").Append(rating!.Value.ToString("0.0", CultureInfo.InvariantCulture)).AppendLine("</rating>");
        }
    }

    private static bool IsUsablePremiered(string? premiered)
    {
        return !string.IsNullOrWhiteSpace(premiered) &&
            DateTime.TryParseExact(premiered, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool IsUsableRating(string? rating)
    {
        return decimal.TryParse(rating, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) && parsed > 0;
    }

    private static bool IsUsableRating(decimal? rating)
    {
        return rating.HasValue && rating.Value > 0;
    }

    private static IEnumerable<string> SplitCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            yield break;
        }

        foreach (var part in csv.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private static bool HasUsableExtendedMetadata(
        string? plot,
        string? genre,
        string? director,
        string? cast,
        string? country,
        string? rating,
        string? premiered,
        string? youtubeTrailerId,
        DateTime? dateAdded)
    {
        return !string.IsNullOrWhiteSpace(plot) ||
            !string.IsNullOrWhiteSpace(genre) ||
            !string.IsNullOrWhiteSpace(director) ||
            !string.IsNullOrWhiteSpace(cast) ||
            !string.IsNullOrWhiteSpace(country) ||
            IsUsableRating(rating) ||
            IsUsablePremiered(premiered) ||
            !string.IsNullOrWhiteSpace(youtubeTrailerId) ||
            dateAdded.HasValue;
    }

    private static bool HasUsableData(VideoInfo? video, AudioInfo? audio)
    {
        bool hasVideo = video != null &&
            (!string.IsNullOrEmpty(video.CodecName) || video.Width > 0 || video.Height > 0);
        bool hasAudio = audio != null &&
            (!string.IsNullOrEmpty(audio.CodecName) || audio.Channels > 0);
        return hasVideo || hasAudio;
    }

    private static decimal? ParseAspectRatio(string aspectRatio)
    {
        if (string.IsNullOrEmpty(aspectRatio))
        {
            return null;
        }

        // Try parsing "16:9" format
        var parts = aspectRatio.Split(':');
        if (parts.Length == 2 &&
            decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var width) &&
            decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var height) &&
            height > 0)
        {
            return width / height;
        }

        // Try parsing decimal format directly
        if (decimal.TryParse(aspectRatio, NumberStyles.Any, CultureInfo.InvariantCulture, out var ratio))
        {
            return ratio;
        }

        return null;
    }

    private static string EscapeXml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}