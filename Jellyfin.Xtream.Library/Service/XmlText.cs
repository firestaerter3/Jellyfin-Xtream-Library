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

using System.Text;
using System.Xml;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Text from the provider on its way into XML the plugin writes (NFO files, Epg.xml).
/// </summary>
internal static class XmlText
{
    /// <summary>
    /// Drops characters XML 1.0 does not allow, such as most control characters. Escaping does not
    /// help with those: one in a title made the whole file unreadable to Jellyfin.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The text without characters XML cannot hold.</returns>
    public static string RemoveInvalidChars(string value)
    {
        int i = 0;
        while (i < value.Length && XmlConvert.IsXmlChar(value[i]))
        {
            i++;
        }

        if (i == value.Length)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        sb.Append(value, 0, i);
        for (; i < value.Length; i++)
        {
            char c = value[i];
            if (XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
            else if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
            {
                sb.Append(c).Append(value[i + 1]);
                i++;
            }
        }

        return sb.ToString();
    }
}
