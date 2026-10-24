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

public class ProgrammeOverlapFilterTests
{
    [Fact]
    public void AnOverlappingRepeatOfTheSameTitleIsDropped()
    {
        var f = new ProgrammeOverlapFilter();
        f.TryKeep("ch", "Zussen", 0, 3000).Should().BeTrue();
        f.TryKeep("ch", " zussen ", 900, 3900).Should().BeFalse();
    }

    [Fact]
    public void BackToBackEpisodesAreKept()
    {
        var f = new ProgrammeOverlapFilter();
        f.TryKeep("ch", "Mickey", 0, 1200).Should().BeTrue();
        f.TryKeep("ch", "Mickey", 1200, 2400).Should().BeTrue();
    }

    [Fact]
    public void OtherChannelsTitlesAndUntitledProgrammesAreKept()
    {
        var f = new ProgrammeOverlapFilter();
        f.TryKeep("a", "News", 0, 1800).Should().BeTrue();
        f.TryKeep("b", "News", 0, 1800).Should().BeTrue();
        f.TryKeep("a", "Sport", 600, 2400).Should().BeTrue();
        f.TryKeep("a", null, 0, 1800).Should().BeTrue();
        f.TryKeep("a", null, 0, 1800).Should().BeTrue();
    }
}
