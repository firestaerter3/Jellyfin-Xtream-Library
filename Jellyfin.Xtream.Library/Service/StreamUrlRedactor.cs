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

using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Removes the provider username and password from an Xtream URL before it is logged.
/// Xtream puts them in the path of stream URLs (/live/user/pass/1.ts) and in the query of API
/// calls (player_api.php?username=…&amp;password=…). Jellyfin's log is what users paste into
/// issues, so neither may reach it.
/// </summary>
public static partial class StreamUrlRedactor
{
    private const string Hidden = "***";

    /// <summary>
    /// Returns the URL with the credentials replaced by ***. A URL without credentials in either
    /// place comes back unchanged. Also covers "username" and "password" fields in a JSON
    /// response, since player_api.php returns both in user_info.
    /// </summary>
    /// <param name="url">The URL to log.</param>
    /// <returns>The URL without credentials.</returns>
    public static string Redact(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        // The credentials sit right before the stream id, so match them there first. Matching the
        // first /live/ in the URL instead masked the wrong two segments when the base URL itself
        // ended in one of these words (http://host/live), and the password was logged.
        var result = TailPathCredentials().Replace(url, "/${t}/" + Hidden + "/" + Hidden + "/");
        if (result == url)
        {
            result = PathCredentials().Replace(url, $"/$1/{Hidden}/{Hidden}/");
        }

        result = QueryCredentials().Replace(result, $"$1{Hidden}");
        return JsonCredentials().Replace(result, $"$1\"{Hidden}\"");
    }

    /// <summary>
    /// Returns the URL with the credentials replaced by ***.
    /// </summary>
    /// <param name="url">The URL to log.</param>
    /// <returns>The URL without credentials.</returns>
    public static string Redact(System.Uri? url) => Redact(url?.OriginalString);

    // The two segments after the stream type are the username and the password. timeshift
    // has more segments after them (duration, start), which are kept. A segment runs to the next
    // '/' only: these URLs are built from the raw password, and a '?' or '#' in it must not end
    // the match and let the whole URL through. A segment can be empty: an unset username
    // gives /live//password/1.ts. A '/' in the password still splits it, but such a
    // URL cannot be played in the first place. Dispatcharr's own API has /api/vod/series/{id}/,
    // which is not a stream URL.
    [GeneratedRegex("(?<!/api/vod)/(live|movie|series|timeshift)/[^/]*/[^/]*/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathCredentials();

    // The same, anchored at the end: user and password followed by the stream id (live, movie,
    // series) or by duration, start and id (timeshift), with an optional trailing slash and query.
    [GeneratedRegex("(?<!/api/vod)/(?<t>live|movie|series)/[^/]*/[^/]*/(?=[^/?#]*/?(?:[?#].*)?$)|(?<!/api/vod)/(?<t>timeshift)/[^/]*/[^/]*/(?=[^/]+/[^/]+/[^/?#]*/?(?:[?#].*)?$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TailPathCredentials();

    [GeneratedRegex("([?&](?:username|password)=)[^&#]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryCredentials();

    // The closing quote is optional: a logged sample may be cut off inside the value.
    [GeneratedRegex("(\"(?:username|password)\"\\s*:\\s*)\"[^\"]*\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonCredentials();
}
