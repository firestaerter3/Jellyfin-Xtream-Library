// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class ContentExclusionFilterTests
{
    [Fact]
    public void BuildSet_Null_ReturnsEmpty()
    {
        ContentExclusionFilter.BuildSet(null).Should().BeEmpty();
    }

    [Fact]
    public void BuildSet_Empty_ReturnsEmpty()
    {
        ContentExclusionFilter.BuildSet(System.Array.Empty<int>()).Should().BeEmpty();
    }

    [Fact]
    public void BuildSet_WithIds_ReturnsThoseIds()
    {
        var set = ContentExclusionFilter.BuildSet(new[] { 1, 2, 2, 3 });
        set.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void IsExcluded_EmptySet_AlwaysFalse()
    {
        var set = ContentExclusionFilter.BuildSet(System.Array.Empty<int>());
        ContentExclusionFilter.IsExcluded(set, 5).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_MatchingId_True()
    {
        var set = ContentExclusionFilter.BuildSet(new[] { 5, 6 });
        ContentExclusionFilter.IsExcluded(set, 5).Should().BeTrue();
        ContentExclusionFilter.IsExcluded(set, 7).Should().BeFalse();
    }
}
