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
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Parses and evaluates the IP/CIDR allow-list for the unauthenticated Live TV endpoints
/// (see issue #109). Format: one entry per line, either a bare address (192.168.1.5,
/// ::1) or a CIDR range (10.0.0.0/8, fd00::/8). Blank lines and lines starting with '#'
/// are ignored, invalid entries are skipped rather than rejecting the whole list.
/// </summary>
public static class IpAllowListParser
{
    private static readonly char[] LineSeparators = ['\n', '\r'];

    /// <summary>
    /// Parses an allow-list from a newline-separated string.
    /// </summary>
    /// <param name="allowListText">Newline-separated list of IPs/CIDR ranges.</param>
    /// <returns>The parsed networks. Empty when <paramref name="allowListText"/> has no valid entries.</returns>
    public static IReadOnlyList<IPNetwork> Parse(string? allowListText)
    {
        var result = new List<IPNetwork>();

        if (string.IsNullOrWhiteSpace(allowListText))
        {
            return result;
        }

        var lines = allowListText.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var entry = line.Trim();
            if (string.IsNullOrEmpty(entry) || entry.StartsWith('#'))
            {
                continue;
            }

            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (IPNetwork.TryParse(entry, out var network))
                {
                    result.Add(network);
                }

                continue;
            }

            // Bare address, no prefix: treat as a single-host network.
            if (IPAddress.TryParse(entry, out var address))
            {
                var prefixLength = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
                result.Add(new IPNetwork(address, prefixLength));
            }
        }

        return result;
    }

    /// <summary>
    /// Decides whether a request from <paramref name="remoteAddress"/> is allowed.
    /// An empty <paramref name="allowList"/> means the endpoint is unrestricted (the
    /// existing, default behaviour). A non-empty list only allows a match; an
    /// unresolvable remote address is treated as not allowed once a list is configured,
    /// never as an open pass.
    /// </summary>
    /// <param name="remoteAddress">The caller's remote IP address, or null if unknown.</param>
    /// <param name="allowList">The parsed allow-list.</param>
    /// <returns>True if the request should be served.</returns>
    public static bool IsAllowed(IPAddress? remoteAddress, IReadOnlyList<IPNetwork> allowList)
    {
        if (allowList.Count == 0)
        {
            return true;
        }

        if (remoteAddress == null)
        {
            return false;
        }

        // Kestrel commonly reports loopback/IPv4 callers as IPv4-mapped IPv6
        // addresses (::ffff:127.0.0.1) on dual-stack sockets. Normalise so a plain
        // "127.0.0.1" or "10.0.0.0/8" entry still matches.
        var candidate = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;

        foreach (var network in allowList)
        {
            if (network.Contains(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
