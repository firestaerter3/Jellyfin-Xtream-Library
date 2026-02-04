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
    /// Writes a movie NFO file with stream details.
    /// </summary>
    /// <param name="nfoPath">Path to the NFO file.</param>
    /// <param name="title">Movie title.</param>
    /// <param name="video">Video stream info.</param>
    /// <param name="audio">Audio stream info.</param>
    /// <param name="durationSecs">Duration in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if NFO was written, false if no media info was available.</returns>
    public static async Task<bool> WriteMovieNfoAsync(
        string nfoPath,
        string title,
        VideoInfo? video,
        AudioInfo? audio,
        int? durationSecs,
        CancellationToken cancellationToken)
    {
        // Skip if no media info available
        if (video == null && audio == null)
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<movie>");
        sb.Append("  <title>").Append(EscapeXml(title)).AppendLine("</title>");

        AppendFileInfo(sb, video, audio, durationSecs);

        sb.AppendLine("</movie>");

        await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Writes an episode NFO file with stream details.
    /// </summary>
    /// <param name="nfoPath">Path to the NFO file.</param>
    /// <param name="seriesName">Series name.</param>
    /// <param name="seasonNumber">Season number.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <param name="episodeTitle">Episode title.</param>
    /// <param name="video">Video stream info.</param>
    /// <param name="audio">Audio stream info.</param>
    /// <param name="durationSecs">Duration in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if NFO was written, false if no media info was available.</returns>
    public static async Task<bool> WriteEpisodeNfoAsync(
        string nfoPath,
        string seriesName,
        int seasonNumber,
        int episodeNumber,
        string? episodeTitle,
        VideoInfo? video,
        AudioInfo? audio,
        int? durationSecs,
        CancellationToken cancellationToken)
    {
        // Skip if no media info available
        if (video == null && audio == null)
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<episodedetails>");
        sb.Append("  <title>").Append(EscapeXml(episodeTitle ?? string.Format(CultureInfo.InvariantCulture, "Episode {0}", episodeNumber))).AppendLine("</title>");
        sb.Append("  <showtitle>").Append(EscapeXml(seriesName)).AppendLine("</showtitle>");
        sb.Append("  <season>").Append(seasonNumber.ToString(CultureInfo.InvariantCulture)).AppendLine("</season>");
        sb.Append("  <episode>").Append(episodeNumber.ToString(CultureInfo.InvariantCulture)).AppendLine("</episode>");

        AppendFileInfo(sb, video, audio, durationSecs);

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
