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

#pragma warning disable CS0618 // Legacy config fields exercised in regression tests for BUG-008
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library;
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

    [Fact]
    public void ChooseCategoryFetchStrategy_IncludeAllMode_AlwaysAllFromProvider()
    {
        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.IncludeAll, selectedCategoryCount: 0)
            .Should().Be(LiveTvService.CategoryFetchStrategy.AllFromProvider);

        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.IncludeAll, selectedCategoryCount: 5)
            .Should().Be(LiveTvService.CategoryFetchStrategy.AllFromProvider);
    }

    [Fact]
    public void ChooseCategoryFetchStrategy_CustomMode_EmptySelection_None()
    {
        // The headline regression-guard test: pre-v1.35 this same input (empty selection)
        // ended up fetching every channel from the provider. Custom mode must now mean "none".
        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.Custom, selectedCategoryCount: 0)
            .Should().Be(LiveTvService.CategoryFetchStrategy.None);
    }

    [Fact]
    public void ChooseCategoryFetchStrategy_CustomMode_NonEmptySelection_BySelectedCategories()
    {
        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.Custom, selectedCategoryCount: 1)
            .Should().Be(LiveTvService.CategoryFetchStrategy.BySelectedCategories);

        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.Custom, selectedCategoryCount: 47)
            .Should().Be(LiveTvService.CategoryFetchStrategy.BySelectedCategories);
    }

    // BUG-008: multi-provider configs (Providers[0] populated, legacy fields empty) used to
    // produce m3u stream URLs shaped like "/live///{streamId}.ts" because BuildStreamUrl
    // read the legacy single-provider fields directly. These tests pin the resolver and the
    // two URL builders to the multi-provider data model.

    [Fact]
    public void ResolveLiveTvProvider_ProvidersPopulated_ReturnsProviderCredentials()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://multi.example.com:5656",
            Username = "multiuser",
            Password = "multipass",
        });

        // Legacy fields left empty — represents a fresh v1.32+ install configured via the
        // multi-provider UI.
        var result = LiveTvService.ResolveLiveTvProvider(config);

        result.BaseUrl.Should().Be("http://multi.example.com:5656");
        result.Username.Should().Be("multiuser");
        result.Password.Should().Be("multipass");
    }

    [Fact]
    public void ResolveLiveTvProvider_LegacyOnly_FallsBackToLegacyFields()
    {
        var config = new PluginConfiguration
        {
            BaseUrl = "http://legacy.example.com",
            Username = "legacyuser",
            Password = "legacypass",
        };

        // Providers list deliberately empty — represents a config caught mid-migration.
        var result = LiveTvService.ResolveLiveTvProvider(config);

        result.BaseUrl.Should().Be("http://legacy.example.com");
        result.Username.Should().Be("legacyuser");
        result.Password.Should().Be("legacypass");
    }

    [Fact]
    public void ResolveLiveTvProvider_BothPopulated_PrefersProviders()
    {
        var config = new PluginConfiguration
        {
            BaseUrl = "http://legacy.example.com",
            Username = "legacyuser",
            Password = "legacypass",
        };
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://multi.example.com:5656",
            Username = "multiuser",
            Password = "multipass",
        });

        var result = LiveTvService.ResolveLiveTvProvider(config);

        result.BaseUrl.Should().Be("http://multi.example.com:5656");
        result.Username.Should().Be("multiuser");
    }

    [Fact]
    public void BuildStreamUrl_MultiProviderOnly_UsesProviderCredentials()
    {
        var config = new PluginConfiguration { LiveTvOutputFormat = "ts" };
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://multi.example.com:5656",
            Username = "multiuser",
            Password = "multipass",
        });

        var channel = new LiveStreamInfo { StreamId = 2420044, Name = "X", Num = 1 };
        var url = LiveTvService.BuildStreamUrl(config, channel);

        url.Should().Be("http://multi.example.com:5656/live/multiuser/multipass/2420044.ts");
        url.Should().NotContain("///");
    }
}
