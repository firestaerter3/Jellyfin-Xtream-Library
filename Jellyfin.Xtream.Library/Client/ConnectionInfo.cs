// Copyright (C) 2022  Kevin Jilissen

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

namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// A wrapper class for Xtream API client connection information.
/// </summary>
/// <param name="baseUrl">The base url including protocol and port number, without trailing slash.</param>
/// <param name="username">The username for authentication.</param>
/// <param name="password">The password for authentication.</param>
/// <remarks>
/// All three values are trimmed. Credentials are interpolated straight into stream URLs, so a
/// stray space saved with the password produces ".../&lt;password&gt; /12345.mkv" and a 403 on every
/// item. Trimming here rather than only in the configuration page also repairs configurations
/// that were already saved with whitespace, without the user having to re-enter anything.
/// </remarks>
public class ConnectionInfo(string baseUrl, string username, string password)
{
    /// <summary>
    /// Gets or sets the base url including protocol and port number, without trailing slash.
    /// </summary>
    public string BaseUrl { get; set; } = (baseUrl ?? string.Empty).Trim();

    /// <summary>
    /// Gets or sets the username for authentication.
    /// </summary>
    public string UserName { get; set; } = (username ?? string.Empty).Trim();

    /// <summary>
    /// Gets or sets the password for authentication.
    /// </summary>
    public string Password { get; set; } = (password ?? string.Empty).Trim();

    /// <inheritdoc />
    public override string ToString() => $"{BaseUrl} {UserName}:***";
}
