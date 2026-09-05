// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #86. Providers number channels from 1 within each category, so the raw number is neither
// a useful sort key across categories nor unique.

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class ChannelNumberingTests
{
    private static LiveStreamInfo Channel(int num, int? categoryId) =>
        new() { StreamId = num, Name = $"Channel {num}", Num = num, CategoryId = categoryId };

    [Fact]
    public void Stride_DefaultsToAThousand_WhenChannelNumbersAreSmall()
    {
        ChannelNumbering.ComputeStride([Channel(1, 10), Channel(999, 10)])
            .Should().Be(ChannelNumbering.MinimumStride);
    }

    [Fact]
    public void Stride_Widens_SoNoCategoryCanSpillIntoTheNext()
    {
        // A provider numbering a channel 1000 would otherwise land it on top of category+1's first
        // channel.
        ChannelNumbering.ComputeStride([Channel(1, 10), Channel(1000, 10)]).Should().Be(10000);
        ChannelNumbering.ComputeStride([Channel(45678, 10)]).Should().Be(100000);
    }

    [Fact]
    public void Resolve_PrefixesTheCategoryId()
    {
        ChannelNumbering.Resolve(Channel(7, 3), 1000, byCategory: true).Should().Be(3007);
    }

    [Fact]
    public void Resolve_LeavesTheNumberAlone_WhenTheFeatureIsOff()
    {
        ChannelNumbering.Resolve(Channel(7, 3), 1000, byCategory: false).Should().Be(7);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public void Resolve_LeavesTheNumberAlone_WhenThereIsNoCategoryToGroupBy(int? categoryId)
    {
        ChannelNumbering.Resolve(Channel(7, categoryId), 1000, byCategory: true).Should().Be(7);
    }

    [Fact]
    public void Resolve_FallsBackToTheRawNumber_RatherThanOverflowing()
    {
        // Reported category ids already reach the 8000s; a widened stride on top of that can leave
        // the int range, and a wrapped number would tune the wrong channel.
        ChannelNumbering.Resolve(Channel(1, 9000), ChannelNumbering.MaximumStride, byCategory: true)
            .Should().Be(1);
    }

    [Fact]
    public void Resolve_KeepsARealisticProviderInsideTheIntRange()
    {
        // The shape the reporter described: category ids in the 8000s.
        ChannelNumbering.Resolve(Channel(12, 8123), 1000, byCategory: true).Should().Be(8123012);
    }

    [Fact]
    public void TwoCategoriesNumberingFromOne_NoLongerCollide()
    {
        // This is the correctness half. The number is the key the tuner resolves hdhr_<number>
        // back to a stream with, so a duplicate makes one of the two channels unreachable.
        var sports = Channel(1, 5);
        var news = Channel(1, 6);

        var stride = ChannelNumbering.ComputeStride([sports, news]);

        ChannelNumbering.Resolve(sports, stride, byCategory: true)
            .Should().NotBe(ChannelNumbering.Resolve(news, stride, byCategory: true));
    }

    [Fact]
    public void Ordering_GroupsEachCategoryTogether()
    {
        var channels = new List<LiveStreamInfo>
        {
            Channel(1, 20), Channel(1, 10), Channel(2, 20), Channel(2, 10),
        };
        var stride = ChannelNumbering.ComputeStride(channels);

        var ordered = channels
            .OrderBy(c => ChannelNumbering.Resolve(c, stride, byCategory: true))
            .Select(c => (c.CategoryId, c.Num))
            .ToList();

        ordered.Should().Equal([(10, 1), (10, 2), (20, 1), (20, 2)]);
    }
}
