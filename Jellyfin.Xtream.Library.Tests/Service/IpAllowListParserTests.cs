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

using System.Net;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class IpAllowListParserTests
{
    #region Parse Tests

    [Fact]
    public void Parse_ReturnsEmptyForNull()
    {
        var result = IpAllowListParser.Parse(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ReturnsEmptyForWhitespace()
    {
        var result = IpAllowListParser.Parse("   ");

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ParsesBareIPv4Address()
    {
        var result = IpAllowListParser.Parse("127.0.0.1");

        result.Should().HaveCount(1);
        result[0].BaseAddress.Should().Be(IPAddress.Parse("127.0.0.1"));
        result[0].PrefixLength.Should().Be(32);
    }

    [Fact]
    public void Parse_ParsesBareIPv6Address()
    {
        var result = IpAllowListParser.Parse("::1");

        result.Should().HaveCount(1);
        result[0].PrefixLength.Should().Be(128);
    }

    [Fact]
    public void Parse_ParsesCidrRange()
    {
        var result = IpAllowListParser.Parse("10.0.0.0/8");

        result.Should().HaveCount(1);
        result[0].PrefixLength.Should().Be(8);
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        var input = "# comment\n\n127.0.0.1\n   \n# another\n10.0.0.0/8";

        var result = IpAllowListParser.Parse(input);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_SkipsInvalidEntriesWithoutThrowing()
    {
        var input = "not-an-ip\n127.0.0.1\n999.999.999.999\n300.0.0.0/8";

        var result = IpAllowListParser.Parse(input);

        result.Should().HaveCount(1);
        result[0].BaseAddress.Should().Be(IPAddress.Parse("127.0.0.1"));
    }

    [Fact]
    public void Parse_HandlesMultipleValidEntries()
    {
        var input = "127.0.0.1\n10.0.0.0/8\n192.168.1.5";

        var result = IpAllowListParser.Parse(input);

        result.Should().HaveCount(3);
    }

    #endregion

    #region IsAllowed Tests

    [Fact]
    public void IsAllowed_EmptyAllowList_AllowsAnyAddress()
    {
        var allowList = IpAllowListParser.Parse(string.Empty);

        var result = IpAllowListParser.IsAllowed(IPAddress.Parse("203.0.113.5"), allowList);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_EmptyAllowList_AllowsNullAddress()
    {
        var allowList = IpAllowListParser.Parse(string.Empty);

        var result = IpAllowListParser.IsAllowed(null, allowList);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_NonEmptyAllowList_RejectsNullAddress()
    {
        var allowList = IpAllowListParser.Parse("127.0.0.1");

        var result = IpAllowListParser.IsAllowed(null, allowList);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_MatchingBareAddress_ReturnsTrue()
    {
        var allowList = IpAllowListParser.Parse("127.0.0.1");

        var result = IpAllowListParser.IsAllowed(IPAddress.Parse("127.0.0.1"), allowList);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_NonMatchingAddress_ReturnsFalse()
    {
        var allowList = IpAllowListParser.Parse("127.0.0.1");

        var result = IpAllowListParser.IsAllowed(IPAddress.Parse("203.0.113.5"), allowList);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_AddressInsideCidrRange_ReturnsTrue()
    {
        var allowList = IpAllowListParser.Parse("10.0.0.0/8");

        var result = IpAllowListParser.IsAllowed(IPAddress.Parse("10.30.30.8"), allowList);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_AddressOutsideCidrRange_ReturnsFalse()
    {
        var allowList = IpAllowListParser.Parse("10.0.0.0/8");

        var result = IpAllowListParser.IsAllowed(IPAddress.Parse("172.16.0.1"), allowList);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_MatchesAnyEntryInMultiLineList()
    {
        var allowList = IpAllowListParser.Parse("127.0.0.1\n10.0.0.0/8\n192.168.1.5");

        IpAllowListParser.IsAllowed(IPAddress.Parse("192.168.1.5"), allowList).Should().BeTrue();
        IpAllowListParser.IsAllowed(IPAddress.Parse("10.5.5.5"), allowList).Should().BeTrue();
        IpAllowListParser.IsAllowed(IPAddress.Parse("192.168.1.6"), allowList).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_IPv4MappedIPv6Loopback_MatchesBareIPv4Entry()
    {
        // Kestrel on a dual-stack socket commonly reports IPv4 callers this way.
        var allowList = IpAllowListParser.Parse("127.0.0.1");
        var mapped = IPAddress.Parse("::ffff:127.0.0.1");

        var result = IpAllowListParser.IsAllowed(mapped, allowList);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_Ipv6Loopback_MatchesBareIPv6Entry()
    {
        var allowList = IpAllowListParser.Parse("::1");

        var result = IpAllowListParser.IsAllowed(IPAddress.Parse("::1"), allowList);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_MismatchedAddressFamilyAgainstCidr_ReturnsFalse()
    {
        var allowList = IpAllowListParser.Parse("10.0.0.0/8");

        var result = IpAllowListParser.IsAllowed(IPAddress.Parse("::1"), allowList);

        result.Should().BeFalse();
    }

    #endregion
}
