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

using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class EpgParsingTests
{
    [Theory]
    [InlineData("20240522100000 +0200", 1716364800)]
    [InlineData("20240522100000 +02:00", 1716364800)]
    [InlineData("202405221000 +0000", 1716372000)]
    [InlineData("20240522 +0000", 1716336000)]
    [InlineData("20240522100000", 1716372000)] // Defaults to +0000
    [InlineData("20240522100000 -0500", 1716390000)]
    [InlineData("20240522100000 -05:00", 1716390000)]
    public void TryParseXmltvTime_HandlesVariousFormats(string input, long expectedUnix)
    {
        var result = LiveTvService.TryParseXmltvTime(input, out var unixSeconds);

        result.Should().BeTrue();
        unixSeconds.Should().Be(expectedUnix);
    }
}
