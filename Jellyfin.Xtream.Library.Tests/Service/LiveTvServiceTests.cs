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
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class LiveTvServiceTests
{
    private readonly LiveTvService _service;

    public LiveTvServiceTests()
    {
        var clientMock = new Mock<IXtreamClient>();
        var appPathsMock = new Mock<IServerApplicationPaths>();
        appPathsMock.Setup(p => p.DataPath).Returns("/tmp");
        var appHostMock = new Mock<IServerApplicationHost>();
        appHostMock.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        _service = new LiveTvService(clientMock.Object, new Mock<IDispatcharrClient>().Object, appPathsMock.Object, appHostMock.Object, NullLogger<LiveTvService>.Instance);
    }

    [Fact]
    public void ResolveChannelLogoUrl_LocalPath_ReturnsProxyUrl()
    {
        _service.ResolveChannelLogoUrl("/share/logo.png", 7)
            .Should().Be("http://127.0.0.1:8096/XtreamLibrary/ChannelLogo/7");
    }

    [Fact]
    public void ResolveChannelLogoUrl_HttpUrl_Unchanged()
    {
        _service.ResolveChannelLogoUrl("http://x/y.png", 7).Should().Be("http://x/y.png");
    }

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

    // GitHub #79: ExcludeSelected reads SelectedLiveCategoryIds as an exclusion list, so that
    // categories the provider adds later are synced without anyone having to retick the list.

    [Fact]
    public void ChooseCategoryFetchStrategy_ExcludeSelectedMode_AlwaysAllFromProvider()
    {
        // The trap this pins: falling through to the Custom branch would fetch *by*
        // SelectedLiveCategoryIds, i.e. exactly the categories the user asked to be rid of.
        // Nothing would throw - the channel list would just be inverted.
        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.ExcludeSelected, selectedCategoryCount: 3)
            .Should().Be(LiveTvService.CategoryFetchStrategy.AllFromProvider);

        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.ExcludeSelected, selectedCategoryCount: 0)
            .Should().Be(LiveTvService.CategoryFetchStrategy.AllFromProvider);
    }

    [Fact]
    public void FilterExcludedCategories_EmptySelection_KeepsEverything()
    {
        // Excluding nothing excludes nothing. Deliberately the opposite of Custom mode's
        // empty-means-none rule, and matching what Movies and Series do (GitHub #76).
        // Do not "fix" this into None by analogy with Custom.
        var channels = MakeCategorisedChannels((1, 10), (2, 20));

        LiveTvService.FilterExcludedCategories(channels, Array.Empty<int>()).Should().HaveCount(2);
        LiveTvService.FilterExcludedCategories(channels, null).Should().BeSameAs(channels);
    }

    [Fact]
    public void FilterExcludedCategories_DropsChannelsInExcludedCategories()
    {
        var channels = MakeCategorisedChannels((1, 10), (2, 20), (3, 10), (4, 30));

        var result = LiveTvService.FilterExcludedCategories(channels, new[] { 10 });

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 2, 4 });
    }

    [Fact]
    public void FilterExcludedCategories_ChannelWithoutCategory_IsKept()
    {
        // No category means it cannot be in the exclusion list.
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Uncategorised", Num = 1, CategoryId = null },
            new() { StreamId = 2, Name = "Excluded", Num = 2, CategoryId = 10 },
        };

        var result = LiveTvService.FilterExcludedCategories(channels, new[] { 10 });

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public void FilterExcludedCategories_HonoursSecondaryCategoryMembership()
    {
        // Some providers put one channel in several categories and report the extras only in
        // category_ids. Include mode fetches per category, so it picks a channel up through any
        // of them; excluding on the same union is what keeps Exclude the exact complement.
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Primary elsewhere", Num = 1, CategoryId = 20, CategoryIds = new[] { 20, 10 } },
            new() { StreamId = 2, Name = "Untouched", Num = 2, CategoryId = 20, CategoryIds = new[] { 20 } },
        };

        var result = LiveTvService.FilterExcludedCategories(channels, new[] { 10 });

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 2 });
    }

    [Fact]
    public void FilterExcludedCategories_DoesNotMutateInput()
    {
        var channels = MakeCategorisedChannels((1, 10), (2, 20));

        LiveTvService.FilterExcludedCategories(channels, new[] { 10 });

        channels.Should().HaveCount(2);
    }

    [Fact]
    public void ApplyRenderTimeFilters_ExcludedCategoriesReappliedOnSnapshotRestore()
    {
        // A snapshot taken before a category was excluded still holds its channels: in this mode
        // the category filter runs after the fetch instead of deciding what to fetch, so the
        // snapshot path has to re-apply it or keep serving what the user just excluded.
        var config = MakeM3UConfig();
        config.LiveChannelMode = LiveChannelSelectionMode.ExcludeSelected;
        config.SelectedLiveCategoryIds = new[] { 10 };
        var channels = MakeCategorisedChannels((1, 10), (2, 20));

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 2 });
    }

    [Fact]
    public void ApplyRenderTimeFilters_PerChannelExclusionsHonouredInExcludeSelectedMode()
    {
        var config = MakeM3UConfig();
        config.LiveChannelMode = LiveChannelSelectionMode.ExcludeSelected;
        config.SelectedLiveCategoryIds = Array.Empty<int>();
        config.ExcludedLiveStreamIds = new[] { 2 };
        var channels = MakeCategorisedChannels((1, 10), (2, 20));

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1 });
    }

    private static List<LiveStreamInfo> MakeCategorisedChannels(params (int StreamId, int CategoryId)[] channels) =>
        channels.Select(c => new LiveStreamInfo
        {
            StreamId = c.StreamId,
            Name = "Channel " + c.StreamId,
            Num = c.StreamId,
            CategoryId = c.CategoryId,
            CategoryIds = new[] { c.CategoryId },
        }).ToList();

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

    [Fact]
    public void BuildStreamUrl_MultipleLiveTvProviders_UsesChannelProviderCredentials()
    {
        var config = new PluginConfiguration { LiveTvOutputFormat = "ts" };
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://provider-a.example.com",
            Username = "user-a",
            Password = "pass-a",
        });
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://provider-b.example.com",
            Username = "user-b",
            Password = "pass-b",
        });

        var channel = new LiveStreamInfo { ProviderIndex = 1, StreamId = 2420044, Name = "X", Num = 1 };
        var url = LiveTvService.BuildStreamUrl(config, channel);

        url.Should().Be("http://provider-b.example.com/live/user-b/pass-b/2420044.ts");
    }

    [Fact]
    public void BuildChannelId_ProviderZero_KeepsLegacyShape()
    {
        XtreamTunerHost.BuildChannelId(providerIndex: 0, streamId: 100).Should().Be("xtream_100");
    }

    [Fact]
    public void BuildChannelId_AdditionalProvider_IncludesProviderIndex()
    {
        XtreamTunerHost.BuildChannelId(providerIndex: 1, streamId: 100).Should().Be("xtream_1_100");
    }

    // BUG-011: Live TV channels appeared as one flat list because GenerateM3U never
    // emitted group-title. These pin the category grouping behaviour.

    private static PluginConfiguration MakeM3UConfig()
    {
        var config = new PluginConfiguration { LiveTvOutputFormat = "ts" };
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://multi.example.com:5656",
            Username = "multiuser",
            Password = "multipass",
        });
        return config;
    }

    // GitHub #112. stream_type reached the models and stopped there, so a radio station was
    // published as a television channel in the M3U and in the native tuner alike.

    [Fact]
    public void GenerateM3U_RadioChannel_EmitsRadioAttribute()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Radio 1", Num = 1, StreamType = "radio_streams" },
        };

        LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>())
            .Should().Contain("radio=\"true\"");
    }

    [Fact]
    public void GenerateM3U_TelevisionChannel_DoesNotEmitRadioAttribute()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, StreamType = "live" },
        };

        LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>())
            .Should().NotContain("radio=");
    }

    [Fact]
    public void GenerateM3U_UnknownStreamType_IsNotTreatedAsRadio()
    {
        // One provider uses created_live across Kids, Movies and Sports. Guessing from anything
        // other than the confirmed value would move real television out of the TV list.
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Kids Channel", Num = 1, StreamType = "created_live" },
        };

        LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>())
            .Should().NotContain("radio=");
    }

    [Fact]
    public void GenerateM3U_RadioFlagSurvivesASnapshotRoundTrip()
    {
        // The M3U is usually rendered from the snapshot rather than a fresh fetch, so a flag that
        // only exists on the live object would come and go between runs.
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Radio 1", Num = 1, StreamType = "radio_streams" },
        };
        var config = MakeM3UConfig();

        var direct = LiveTvService.GenerateM3U(channels, config, catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>());
        var restored = LiveTvService.GenerateM3U(
            LiveChannelSnapshot.FromChannels(channels).ToChannels(),
            config,
            catchupOnly: false,
            "http://127.0.0.1:8096",
            new Dictionary<int, string>());

        restored.Should().Be(direct);
        restored.Should().Contain("radio=\"true\"");
    }

    // GitHub #113. These playlists are served without a Jellyfin login, so every credential in one
    // is handed to whoever fetches it. A channel Dispatcharr can be matched to has a URL that needs
    // no credentials at all.

    private static PluginConfiguration MakeDispatcharrConfig()
    {
        var config = new PluginConfiguration { LiveTvOutputFormat = "ts" };
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://xtream.example.com:8080",
            Username = "multiuser",
            Password = "s3cr3t",
            EnableDispatcharrMode = true,
            DispatcharrBaseUrl = "http://dispatcharr.example.com:9191",
            DispatcharrApiUser = "admin",
        });
        return config;
    }

    [Fact]
    public void GenerateM3U_MatchedChannel_UsesTheProxyUrlAndLeaksNothing()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, DispatcharrUuid = "uuid-one" },
        };

        var m3u = LiveTvService.GenerateM3U(channels, MakeDispatcharrConfig(), catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>());

        m3u.Should().Contain("http://dispatcharr.example.com:9191/proxy/ts/stream/uuid-one");
        m3u.Should().NotContain("s3cr3t", "a playlist anyone can fetch must not carry the provider password");
        m3u.Should().NotContain("multiuser");
    }

    [Fact]
    public void GenerateM3U_UnmatchedChannel_FallsBackRatherThanDisappearing()
    {
        // A channel that worked yesterday must still work today. The cost is that its line keeps
        // the credentials, which is what CountCredentialBearingChannels reports.
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 2, Name = "Channel 2", Num = 2 },
        };

        var m3u = LiveTvService.GenerateM3U(channels, MakeDispatcharrConfig(), catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>());

        m3u.Should().Contain("/live/multiuser/s3cr3t/2.ts");
        m3u.Should().NotContain("/proxy/ts/stream/");
    }

    [Fact]
    public void GenerateM3U_ProxyUrlUsesDispatcharrsOwnHostNotTheXtreamOne()
    {
        // The whole reason DispatcharrBaseUrl exists is that they are not always the same host
        // (GitHub #83). Getting this wrong points the playlist at a host that cannot serve it.
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, DispatcharrUuid = "uuid-one" },
        };

        var m3u = LiveTvService.GenerateM3U(channels, MakeDispatcharrConfig(), catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>());

        m3u.Should().Contain("http://dispatcharr.example.com:9191/proxy/ts/stream/");
        m3u.Should().NotContain("http://xtream.example.com:8080/proxy/");
    }

    [Fact]
    public void GenerateM3U_TheProxyUrlSurvivesASnapshotRoundTrip()
    {
        // The M3U is usually rendered from the stored snapshot rather than a fresh fetch. A uuid
        // held only in memory would produce a credential-free playlist on the runs that reached
        // Dispatcharr and a credentialed one on the runs served from disk, alternating with
        // nothing to explain it. Same trap as stream_type in #112.
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, DispatcharrUuid = "uuid-one" },
        };
        var config = MakeDispatcharrConfig();

        var direct = LiveTvService.GenerateM3U(channels, config, catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>());
        var restored = LiveTvService.GenerateM3U(
            LiveChannelSnapshot.FromChannels(channels).ToChannels(),
            config,
            catchupOnly: false,
            "http://127.0.0.1:8096",
            new Dictionary<int, string>());

        restored.Should().Be(direct);
        restored.Should().Contain("/proxy/ts/stream/uuid-one");
        restored.Should().NotContain("s3cr3t");
    }

    [Fact]
    public void GenerateM3U_WithNoDispatcharrMatches_IsUnchangedFromBefore()
    {
        // Nothing about this feature may alter the output for someone not using Dispatcharr.
        var channels = new List<LiveStreamInfo> { new() { StreamId = 1, Name = "Channel 1", Num = 1 } };

        LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>())
            .Should().Contain("/live/multiuser/");
    }

    [Fact]
    public void CountCredentialBearingChannels_CountsOnlyTheUnmatchedOnes()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, DispatcharrUuid = "uuid-one" },
            new() { StreamId = 2 },
            new() { StreamId = 3, DispatcharrUuid = string.Empty },
        };

        LiveTvService.CountCredentialBearingChannels(channels).Should().Be(2);
        LiveTvService.CountCredentialBearingChannels(null).Should().Be(0);
    }

    // GitHub #115. The last lines carrying the password after #113 were all catchup-source, a
    // template an external client fills in and plays itself. Dispatcharr's /proxy/catchup/{uuid}
    // answers 401 to an anonymous caller where /proxy/ts/stream/{uuid} answers 404, measured
    // against 0.30.0, so there is no credential-free template to put there instead.

    private static List<LiveStreamInfo> MakeArchiveChannel(string uuid) => new()
    {
        new()
        {
            StreamId = 1,
            Name = "Channel 1",
            Num = 1,
            TvArchive = true,
            TvArchiveDuration = 3,
            DispatcharrUuid = uuid,
        },
    };

    private static PluginConfiguration MakeCatchupDispatcharrConfig(bool optIn = false)
    {
        var config = MakeDispatcharrConfig();
        config.EnableCatchup = true;
        config.EmitCredentialedCatchupSource = optIn;
        return config;
    }

    [Fact]
    public void GenerateM3U_MatchedChannelWithArchive_LeavesTheCatchupTemplateOut()
    {
        var m3u = LiveTvService.GenerateM3U(
            MakeArchiveChannel("uuid-one"),
            MakeCatchupDispatcharrConfig(),
            catchupOnly: false,
            "http://127.0.0.1:8096",
            new Dictionary<int, string>());

        m3u.Should().NotContain("catchup-source");
        m3u.Should().NotContain("s3cr3t", "a playlist anyone can fetch must not carry the provider password");
        m3u.Should().Contain("/proxy/ts/stream/uuid-one");

        // catchup="default" without a source tells the client to build the URL from the stream URL,
        // which is the proxy form and has no archive behind it. All three attributes go together.
        m3u.Should().NotContain("catchup=");
        m3u.Should().NotContain("catchup-days");
    }

    [Fact]
    public void GenerateM3U_UnmatchedChannelWithArchive_KeepsItsCatchupTemplate()
    {
        // This line kept its Xtream stream URL in #113, so it already carries the password.
        // Taking its catch-up away as well would cost a working feature and remove nothing.
        var m3u = LiveTvService.GenerateM3U(
            MakeArchiveChannel(string.Empty),
            MakeCatchupDispatcharrConfig(),
            catchupOnly: false,
            "http://127.0.0.1:8096",
            new Dictionary<int, string>());

        m3u.Should().Contain("catchup-source=");
        m3u.Should().Contain("/timeshift/multiuser/s3cr3t/");
    }

    [Fact]
    public void GenerateM3U_WithTheOptIn_MatchedChannelGetsItsTemplateBack()
    {
        var m3u = LiveTvService.GenerateM3U(
            MakeArchiveChannel("uuid-one"),
            MakeCatchupDispatcharrConfig(optIn: true),
            catchupOnly: false,
            "http://127.0.0.1:8096",
            new Dictionary<int, string>());

        m3u.Should().Contain("catchup-source=");
        m3u.Should().Contain("catchup=\"default\"");

        // The stream URL stays credential free; only the template the user asked for carries them.
        m3u.Should().Contain("/proxy/ts/stream/uuid-one");
    }

    [Fact]
    public void GenerateM3U_WithoutDispatcharr_CatchupIsUnchangedFromBefore()
    {
        // Nothing about this may alter the output for someone not using Dispatcharr, and with no
        // uuid on any channel that is what falls out.
        var config = MakeM3UConfig();
        config.EnableCatchup = true;

        var m3u = LiveTvService.GenerateM3U(
            MakeArchiveChannel(string.Empty),
            config,
            catchupOnly: false,
            "http://127.0.0.1:8096",
            new Dictionary<int, string>());

        m3u.Should().Contain("catchup-source=");
        m3u.Should().Contain("catchup-days=\"3\"");
    }

    [Fact]
    public void GenerateM3U_CatchupPlaylist_StillListsTheChannelWithoutATemplate()
    {
        // The cost of this change, pinned rather than left implicit: Catchup.m3u keeps listing the
        // channel, with a credential-free stream URL and nothing telling a client about the archive.
        var m3u = LiveTvService.GenerateM3U(
            MakeArchiveChannel("uuid-one"),
            MakeCatchupDispatcharrConfig(),
            catchupOnly: true,
            "http://127.0.0.1:8096",
            new Dictionary<int, string>());

        m3u.Should().Contain("Channel 1");
        m3u.Should().Contain("/proxy/ts/stream/uuid-one");
        m3u.Should().NotContain("catchup-source");
    }

    [Fact]
    public void GenerateM3U_TheCatchupDecisionSurvivesASnapshotRoundTrip()
    {
        // The M3U normally comes from the snapshot, so a decision made from fields that do not
        // persist would alternate between runs with nothing to explain it. Same trap as #112.
        var channels = MakeArchiveChannel("uuid-one");
        var config = MakeCatchupDispatcharrConfig();

        var direct = LiveTvService.GenerateM3U(channels, config, catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>());
        var restored = LiveTvService.GenerateM3U(
            LiveChannelSnapshot.FromChannels(channels).ToChannels(),
            config,
            catchupOnly: false,
            "http://127.0.0.1:8096",
            new Dictionary<int, string>());

        restored.Should().Be(direct);
        restored.Should().NotContain("catchup-source");
    }

    [Fact]
    public void ShouldEmitCatchupSource_OptInOverridesEverything()
    {
        var matched = MakeArchiveChannel("uuid-one")[0];
        var unmatched = MakeArchiveChannel(string.Empty)[0];

        LiveTvService.ShouldEmitCatchupSource(MakeCatchupDispatcharrConfig(), matched).Should().BeFalse();
        LiveTvService.ShouldEmitCatchupSource(MakeCatchupDispatcharrConfig(), unmatched).Should().BeTrue();
        LiveTvService.ShouldEmitCatchupSource(MakeCatchupDispatcharrConfig(optIn: true), matched).Should().BeTrue();
        LiveTvService.ShouldEmitCatchupSource(MakeCatchupDispatcharrConfig(optIn: true), unmatched).Should().BeTrue();
    }

    [Fact]
    public void CountCredentialedCatchupChannels_CountsOnlyWhatTheOptInPutBack()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, DispatcharrUuid = "uuid-one", TvArchive = true, TvArchiveDuration = 3 },
            new() { StreamId = 2, DispatcharrUuid = "uuid-two", TvArchive = true, TvArchiveDuration = 3 },

            // No archive, so no template either way.
            new() { StreamId = 3, DispatcharrUuid = "uuid-three" },

            // Unmatched: its stream URL already carries the credentials, so it is the other count's.
            new() { StreamId = 4, TvArchive = true, TvArchiveDuration = 3 },
        };

        LiveTvService.CountCredentialedCatchupChannels(MakeCatchupDispatcharrConfig(optIn: true), channels)
            .Should().Be(2);
        LiveTvService.CountCredentialedCatchupChannels(MakeCatchupDispatcharrConfig(), channels)
            .Should().Be(0);

        var catchupOff = MakeCatchupDispatcharrConfig(optIn: true);
        catchupOff.EnableCatchup = false;
        LiveTvService.CountCredentialedCatchupChannels(catchupOff, channels).Should().Be(0);
        LiveTvService.CountCredentialedCatchupChannels(catchupOff, null).Should().Be(0);
    }

    [Fact]
    public void SelectPlaylistChannels_CatchupOnlyKeepsTheArchiveChannels()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, TvArchive = true, TvArchiveDuration = 3 },
            new() { StreamId = 2 },
            new() { StreamId = 3, TvArchive = true, TvArchiveDuration = 0 },
        };

        LiveTvService.SelectPlaylistChannels(channels, catchupOnly: true)
            .Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1 });
        LiveTvService.SelectPlaylistChannels(channels, catchupOnly: false).Should().BeSameAs(channels);
    }

    [Fact]
    public void GenerateM3U_ChannelWithKnownCategory_EmitsGroupTitle()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, CategoryId = 10 },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Sports" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        m3u.Should().Contain("group-title=\"Sports\"");
    }

    // Cold-cache handling renders the M3U from the persisted snapshot instead of doing a full
    // upstream fetch inside the request, because Jellyfin only allows its own M3U fetch 100
    // seconds. That only holds up if a snapshot round trip reproduces the same output.

    [Fact]
    public void GenerateM3U_FromSnapshotRestoredChannels_ByteForByteIdentical()
    {
        var config = MakeM3UConfig();
        config.EnableCatchup = true;
        config.CatchupDays = 7;
        var channels = new List<LiveStreamInfo>
        {
            new()
            {
                StreamId = 1, Name = "BBC One", Num = 1, EpgChannelId = "bbc.one",
                StreamIcon = "http://logos.example.com/bbc.png", CategoryId = 10,
            },
            new()
            {
                StreamId = 2, Name = "Sky Sports", Num = 2, CategoryId = 20,
                TvArchive = true, TvArchiveDuration = 5,
            },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "General", [20] = "Sports" };
        const string BaseUrl = "http://127.0.0.1:8096";

        var fromLiveFetch = LiveTvService.GenerateM3U(channels, config, catchupOnly: false, BaseUrl, categoryNames);

        var snapshot = LiveChannelSnapshot.FromChannels(channels, categoryNames);
        var restored = snapshot.ToChannels();
        restored.Should().NotBeNull();
        var fromSnapshot = LiveTvService.GenerateM3U(restored!, config, catchupOnly: false, BaseUrl, snapshot.Categories);

        fromSnapshot.Should().Be(fromLiveFetch);
    }

    [Fact]
    public void TryBeginChannelRefresh_SecondCallerBlocked_UntilTheFirstEnds()
    {
        // Without this guard every request arriving during a slow refresh would kick off
        // another one, which is the stampede the old lock-the-whole-request design avoided.
        _service.TryBeginChannelRefresh().Should().BeTrue();
        _service.TryBeginChannelRefresh().Should().BeFalse();

        _service.EndChannelRefresh();

        _service.TryBeginChannelRefresh().Should().BeTrue();
    }

    // A successful refresh calls InvalidateCache, so the next tuner poll finds a cold cache
    // again. Starting a refresh on every cold poll therefore loops forever and re-downloads
    // the whole catalogue at poll frequency. Only refresh when the snapshot is actually due.

    // Fetch-time filters are baked into the snapshot, so turning a filter ON after a snapshot
    // was taken would keep serving the filtered-out channels until the next refresh completes.
    // For adult content that is not an acceptable staleness window, so the filters are
    // re-applied when rendering from a snapshot.

    [Fact]
    public void ApplyRenderTimeFilters_AdultChannelsDroppedWhenNotIncluded()
    {
        var config = MakeM3UConfig();
        config.IncludeAdultChannels = false;
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "BBC One", Num = 1 },
            new() { StreamId = 2, Name = "Adult Channel", Num = 2, IsAdult = true },
        };

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public void ApplyRenderTimeFilters_AdultChannelsKeptWhenIncluded()
    {
        var config = MakeM3UConfig();
        config.IncludeAdultChannels = true;
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "BBC One", Num = 1 },
            new() { StreamId = 2, Name = "Adult Channel", Num = 2, IsAdult = true },
        };

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void ApplyRenderTimeFilters_ExclusionsReappliedInCustomMode()
    {
        var config = MakeM3UConfig();
        config.LiveChannelMode = LiveChannelSelectionMode.Custom;
        config.ExcludedLiveStreamIds = new[] { 2 };
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "BBC One", Num = 1 },
            new() { StreamId = 2, Name = "Newly Excluded", Num = 2 },
        };

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public void ApplyRenderTimeFilters_ExclusionsIgnoredInIncludeAllMode()
    {
        // Matches the fetch path, where IncludeAll deliberately ignores per-channel exclusions.
        var config = MakeM3UConfig();
        config.LiveChannelMode = LiveChannelSelectionMode.IncludeAll;
        config.ExcludedLiveStreamIds = new[] { 2 };
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "BBC One", Num = 1 },
            new() { StreamId = 2, Name = "Excluded But IncludeAll", Num = 2 },
        };

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Should().HaveCount(2);
    }

    // The stampede guard only covers the background refresh. The scheduled sync calls
    // RefreshChannelsAsync directly, and both fetch outside the snapshot lock, so a slow fetch
    // that finishes last would overwrite a newer snapshot with older data.

    [Fact]
    public void ShouldWriteSnapshot_NoExistingSnapshot_Writes()
    {
        var started = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldWriteSnapshot(started, existingCreatedAtUtc: null).Should().BeTrue();
    }

    [Fact]
    public void ShouldWriteSnapshot_ExistingIsOlderThanOurFetch_Writes()
    {
        var started = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldWriteSnapshot(started, started.AddMinutes(-5)).Should().BeTrue();
    }

    [Fact]
    public void ShouldWriteSnapshot_NewerSnapshotLandedWhileWeFetched_DoesNotOverwrite()
    {
        // Our fetch started at 12:00 and something wrote a fresher snapshot at 12:02 while we
        // were still downloading. Writing ours now would move the data backwards.
        var started = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldWriteSnapshot(started, started.AddMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void ShouldRefreshSnapshot_JustRefreshed_DoesNotRefreshAgain()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldRefreshSnapshot(now.AddMinutes(-1), now, cacheMinutes: 15)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldRefreshSnapshot_OlderThanCacheWindow_Refreshes()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldRefreshSnapshot(now.AddMinutes(-16), now, cacheMinutes: 15)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldRefreshSnapshot_NoTimestamp_Refreshes()
    {
        // A snapshot written before CreatedAt was populated, or a corrupt one, must not pin
        // the plugin to stale data forever.
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldRefreshSnapshot(default, now, cacheMinutes: 15).Should().BeTrue();
    }

    [Fact]
    public void ShouldRefreshSnapshot_ClockWentBackwards_DoesNotRefreshEveryPoll()
    {
        // A snapshot stamped in the future (clock skew, timezone bug) must not read as
        // infinitely stale, which would reintroduce the refresh-per-poll loop.
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldRefreshSnapshot(now.AddHours(1), now, cacheMinutes: 15)
            .Should().BeFalse();
    }

    [Fact]
    public void GenerateM3U_ChannelWithUnknownCategoryId_OmitsGroupTitle()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, CategoryId = 99 },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Sports" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        m3u.Should().NotContain("group-title");
    }

    [Fact]
    public void GenerateM3U_ChannelWithNullCategoryId_OmitsGroupTitle()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, CategoryId = null },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Sports" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        m3u.Should().NotContain("group-title");
    }

    // GitHub #123. M3U is not XML and nothing that reads it decodes entities, so an escaped
    // ampersand was shown to users as the literal text "&amp;".
    [Fact]
    public void GenerateM3U_AmpersandIsWrittenAsIs()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "News & Weather", Num = 1, CategoryId = 10, EpgChannelId = "news&weather.uk" },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Sports & News" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        m3u.Should().Contain("group-title=\"Sports & News\"");
        m3u.Should().Contain("tvg-name=\"News & Weather\"");
        m3u.Should().Contain("tvg-id=\"news&weather.uk\"", "the EPG decodes its channel id, so the playlist has to carry the same text to match");
        m3u.Should().NotContain("&amp;");
    }

    // A logo URL with a query string stopped working once its separators became "&amp;".
    [Fact]
    public void GenerateM3U_LogoUrlQueryStringSurvives()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, StreamIcon = "http://img.test/logo.png?w=100&h=100" },
        };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", new Dictionary<int, string>());

        m3u.Should().Contain("tvg-logo=\"http://img.test/logo.png?w=100&h=100\"");
    }

    // A double quote would end the attribute early. "&quot;" is no better, it is shown literally,
    // and the old replacement order turned it into "&amp;quot;".
    [Fact]
    public void GenerateM3U_DoubleQuoteBecomesSingleQuote()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, CategoryId = 10 },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Kids \"Best Of\"" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        m3u.Should().Contain("group-title=\"Kids 'Best Of'\"");
        m3u.Should().NotContain("&quot;");
    }

    // Each channel is two lines, the #EXTINF and its URL. A line break inside a provider's name
    // splits the entry and makes the rest of the name read as a stream address.
    [Fact]
    public void GenerateM3U_LineBreakInANameStaysOnOneLine()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel\r\nhttp://elsewhere.test/x", Num = 1, CategoryId = 10 },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Sports\nNews" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        var lines = m3u.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().NotContain(l => l.TrimEnd('\r').StartsWith("http://elsewhere.test", StringComparison.Ordinal));
        m3u.Should().Contain("group-title=\"Sports News\"");
    }

    // Live TV never applied the per-provider client tuning at all, so every Live TV call ran
    // on the client defaults regardless of what the provider was configured with. That is the
    // path the timeout has to cover, because that is where the catalog fetch happens.

    [Fact]
    public void ResolveClientTimeout_SingleProvider_UsesThatProvidersTimeout()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://one.example.com",
            Username = "user",
            TimeoutSeconds = 600,
        });

        LiveTvService.ResolveClientTimeout(config).Should().Be(TimeSpan.FromSeconds(600));
    }

    [Fact]
    public void ResolveClientTimeout_MultipleProviders_UsesTheLargest()
    {
        // One client serves every provider in a single operation, so timing out at the
        // smallest value would cut off the slowest provider mid-fetch.
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://fast.example.com",
            Username = "user",
            TimeoutSeconds = 120,
        });
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://slow.example.com",
            Username = "user",
            TimeoutSeconds = 900,
        });

        LiveTvService.ResolveClientTimeout(config).Should().Be(TimeSpan.FromSeconds(900));
    }

    [Fact]
    public void ResolveClientTimeout_DisabledProviderIgnored()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://enabled.example.com",
            Username = "user",
            TimeoutSeconds = 120,
        });
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://disabled.example.com",
            Username = "user",
            IsEnabled = false,
            TimeoutSeconds = 900,
        });

        LiveTvService.ResolveClientTimeout(config).Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void ResolveClientTimeout_NoUsableProviders_FallsBackToDefault()
    {
        LiveTvService.ResolveClientTimeout(new PluginConfiguration()).Should().Be(TimeSpan.FromMinutes(5));
    }
}
