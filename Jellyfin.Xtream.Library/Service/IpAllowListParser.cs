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
using System.Linq;
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

            var slash = entry.IndexOf('/', StringComparison.Ordinal);
            var addressPart = slash >= 0 ? entry[..slash] : entry;

            // IPAddress.TryParse accepts shortened IPv4 ("192.168.1" is 192.168.0.1, "10" is
            // 0.0.0.10), so a half-typed LAN entry would quietly allow some other host instead of
            // being rejected. IPv4 has to be written out in full.
            if (!addressPart.Contains(':', StringComparison.Ordinal) && addressPart.Split('.').Length != 4)
            {
                continue;
            }

            if (slash >= 0)
            {
                if (IPNetwork.TryParse(entry, out var network))
                {
                    result.Add(Normalize(network));
                }

                continue;
            }

            // Bare address, no prefix: treat as a single-host network.
            if (IPAddress.TryParse(entry, out var address))
            {
                var prefixLength = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
                result.Add(Normalize(new IPNetwork(address, prefixLength)));
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the text holds any entry at all, valid or not: a line that is neither blank nor a
    /// '#' comment. A list with only comments is not restricting anything, while one whose
    /// entries all fail to parse is a typo and has to deny everyone.
    /// </summary>
    /// <param name="allowListText">The allow-list text.</param>
    /// <returns>True when at least one non-comment line is present.</returns>
    public static bool HasEntries(string? allowListText)
        => !string.IsNullOrWhiteSpace(allowListText) &&
           allowListText.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries)
               .Select(l => l.Trim())
               .Any(l => l.Length > 0 && !l.StartsWith('#'));

    /// <summary>
    /// The address a caller is matched as: an IPv4-mapped IPv6 address (::ffff:a.b.c.d, how
    /// Kestrel reports IPv4 callers on a dual-stack socket) as plain IPv4.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>The address used for matching and logging.</returns>
    public static IPAddress Canonical(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    // An entry copied from a log line in mapped form (::ffff:a.b.c.d) has to match the IPv4 form
    // callers are compared in.
    private static IPNetwork Normalize(IPNetwork network)
        => network.BaseAddress.IsIPv4MappedToIPv6 && network.PrefixLength >= 96
            ? new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96)
            : network;

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
        var candidate = Canonical(remoteAddress);

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
