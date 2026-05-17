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
using FluentAssertions;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class LiveTvServiceTests
{
    private static List<LiveStreamInfo> MakeChannels(params int[] streamIds) =>
        streamIds.Select(id => new LiveStreamInfo
        {
            StreamId = id,
            Name = "Channel " + id,
            Num = id,
        }).ToList();

    [Fact]
    public void FilterExcludedChannels_EmptyExclusionList_ReturnsAllChannels()
    {
        var channels = MakeChannels(1, 2, 3);

        var result = LiveTvService.FilterExcludedChannels(channels, Array.Empty<int>());

        result.Should().HaveCount(3);
        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void FilterExcludedChannels_NullExclusionList_ReturnsAllChannels()
    {
        var channels = MakeChannels(1, 2, 3);

        var result = LiveTvService.FilterExcludedChannels(channels, null);

        result.Should().BeSameAs(channels);
    }

    [Fact]
    public void FilterExcludedChannels_ExcludesSpecifiedStreamIds()
    {
        var channels = MakeChannels(1, 2, 3, 4, 5);

        var result = LiveTvService.FilterExcludedChannels(channels, new[] { 2, 4 });

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1, 3, 5 });
    }

    [Fact]
    public void FilterExcludedChannels_StaleExclusionIds_DoesNotThrow()
    {
        // Channel 99 doesn't exist in the source list — must not error or affect output.
        var channels = MakeChannels(1, 2, 3);

        var result = LiveTvService.FilterExcludedChannels(channels, new[] { 99 });

        result.Should().HaveCount(3);
    }

    [Fact]
    public void FilterExcludedChannels_AllExcluded_ReturnsEmpty()
    {
        var channels = MakeChannels(1, 2);

        var result = LiveTvService.FilterExcludedChannels(channels, new[] { 1, 2 });

        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterExcludedChannels_DoesNotMutateInput()
    {
        var channels = MakeChannels(1, 2, 3);

        LiveTvService.FilterExcludedChannels(channels, new[] { 2 });

        channels.Should().HaveCount(3);
    }
}
