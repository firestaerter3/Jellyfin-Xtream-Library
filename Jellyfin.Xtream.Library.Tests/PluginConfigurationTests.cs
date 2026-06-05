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

using System.IO;
using System.Xml.Serialization;
using FluentAssertions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests;

public class PluginConfigurationTests
{
    // =====================
    // ProviderConfig validation (per-provider fields)
    // =====================

    [Fact]
    public void ProviderConfig_Validate_ClampsSyncParallelism_ToValidRange()
    {
        var provider = new ProviderConfig { SyncParallelism = 50 };
        provider.Validate();
        provider.SyncParallelism.Should().Be(20);
    }

    [Fact]
    public void ProviderConfig_Validate_ClampsSyncParallelism_ToMinimumOne()
    {
        var provider = new ProviderConfig { SyncParallelism = 0 };
        provider.Validate();
        provider.SyncParallelism.Should().Be(1);
    }

    [Fact]
    public void ProviderConfig_Validate_ClampsOrphanSafetyThreshold_ToValidRange()
    {
        var p1 = new ProviderConfig { OrphanSafetyThreshold = 1.5 };
        var p2 = new ProviderConfig { OrphanSafetyThreshold = -0.1 };
        p1.Validate();
        p2.Validate();
        p1.OrphanSafetyThreshold.Should().Be(1.0);
        p2.OrphanSafetyThreshold.Should().Be(0.0);
    }

    [Fact]
    public void ProviderConfig_Validate_ClampsRequestDelayMs_ToNonNegative()
    {
        var provider = new ProviderConfig { RequestDelayMs = -100 };
        provider.Validate();
        provider.RequestDelayMs.Should().Be(0);
    }

    [Fact]
    public void ProviderConfig_Validate_ClampsCategoryBatchSize_ToValidRange()
    {
        var provider = new ProviderConfig { CategoryBatchSize = 150 };
        provider.Validate();
        provider.CategoryBatchSize.Should().Be(100);
    }

    [Fact]
    public void ProviderConfig_Validate_ClampsFullSyncIntervalDays_ToValidRange()
    {
        var pLow = new ProviderConfig { FullSyncIntervalDays = 0 };
        var pHigh = new ProviderConfig { FullSyncIntervalDays = 60 };
        pLow.Validate();
        pHigh.Validate();
        pLow.FullSyncIntervalDays.Should().Be(1);
        pHigh.FullSyncIntervalDays.Should().Be(30);
    }

    [Fact]
    public void ProviderConfig_Validate_ClampsFullSyncChangeThreshold_ToValidRange()
    {
        var pLow = new ProviderConfig { FullSyncChangeThreshold = -0.5 };
        var pHigh = new ProviderConfig { FullSyncChangeThreshold = 2.0 };
        pLow.Validate();
        pHigh.Validate();
        pLow.FullSyncChangeThreshold.Should().Be(0.0);
        pHigh.FullSyncChangeThreshold.Should().Be(1.0);
    }

    [Fact]
    public void ProviderConfig_DefaultValues_AreReasonable()
    {
        var provider = new ProviderConfig();
        provider.Validate();
        provider.SyncParallelism.Should().Be(10);
        provider.OrphanSafetyThreshold.Should().Be(0.20);
        provider.RequestDelayMs.Should().Be(50);
        provider.MaxRetries.Should().Be(3);
        provider.RetryDelayMs.Should().Be(1000);
        provider.EnableIncrementalSync.Should().BeTrue();
        provider.FullSyncIntervalDays.Should().Be(7);
        provider.FullSyncChangeThreshold.Should().Be(0.50);
    }

    [Fact]
    public void ProviderConfig_FallbackToYearlessLookup_DefaultIsFalse()
    {
        var provider = new ProviderConfig();
        provider.FallbackToYearlessLookup.Should().BeFalse();
    }

    // =====================
    // PluginConfiguration validation (global fields)
    // =====================

    [Fact]
    public void Validate_ClampsMetadataParallelism_ToValidRange()
    {
        var config = new PluginConfiguration { MetadataParallelism = 15 };
        config.Validate();
        config.MetadataParallelism.Should().Be(10);
    }

    [Fact]
    public void Validate_ClampsDailySchedule_ToValidTimeRange()
    {
        var config = new PluginConfiguration { SyncDailyHour = 25, SyncDailyMinute = 70 };
        config.Validate();
        config.SyncDailyHour.Should().Be(23);
        config.SyncDailyMinute.Should().Be(59);
    }

    [Fact]
    public void Validate_GlobalDefaultValues_AreReasonable()
    {
        var config = new PluginConfiguration();
        config.Validate();
        config.MetadataParallelism.Should().Be(3);
        config.SyncIntervalMinutes.Should().Be(60);
        config.EnableMetadataLookup.Should().BeTrue();
    }

    [Fact]
    public void Validate_DelegatesToEachProvider()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig { SyncParallelism = 999 });
        config.Validate();
        config.Providers[0].SyncParallelism.Should().Be(20);
    }

    [Fact]
    public void Validate_DetectsDuplicateLibraryPaths()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://p1.com", LibraryPath = "/shared" });
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://p2.com", LibraryPath = "/shared" });
        config.Validate();
        config.HasDuplicateLibraryPaths.Should().BeTrue();
    }

    [Fact]
    public void Validate_NoDuplicateLibraryPaths_WhenPathsDistinct()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://p1.com", LibraryPath = "/lib1" });
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://p2.com", LibraryPath = "/lib2" });
        config.Validate();
        config.HasDuplicateLibraryPaths.Should().BeFalse();
    }

    [Fact]
    public void GetLiveTvProvider_ReturnsFirstProviderWithBaseUrl()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig { BaseUrl = string.Empty });
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://provider2.com" });
        var liveTvProvider = config.GetLiveTvProvider();
        liveTvProvider.Should().NotBeNull();
        liveTvProvider!.BaseUrl.Should().Be("http://provider2.com");
    }

    [Fact]
    public void GetLiveTvProvider_ReturnsNull_WhenNoProviders()
    {
        var config = new PluginConfiguration();
        config.GetLiveTvProvider().Should().BeNull();
    }

    // =====================
    // XML deserialization of legacy single-provider config (Phase 5 migration source)
    // .NET's XmlSerializer silently skips [Obsolete] properties on deserialization,
    // so legacy fields must NOT carry that attribute or migration breaks.
    // =====================

    [Fact]
    public void XmlDeserialize_LegacyV131Schema_PopulatesLegacyFields()
    {
        const string legacyXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <BaseUrl>http://provider.example:8080</BaseUrl>
  <Username>alice</Username>
  <Password>secret</Password>
  <LibraryPath>/config/xtream-library</LibraryPath>
  <SyncMovies>true</SyncMovies>
  <SyncSeries>true</SyncSeries>
  <SelectedVodCategoryIds />
  <SelectedSeriesCategoryIds />
  <EnableDispatcharrMode>true</EnableDispatcharrMode>
  <DispatcharrApiUser>alice</DispatcharrApiUser>
  <DispatcharrApiPass>dispatcharr-pass</DispatcharrApiPass>
</PluginConfiguration>";

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(legacyXml);
        var config = (PluginConfiguration)serializer.Deserialize(reader)!;

        config.BaseUrl.Should().Be("http://provider.example:8080");
        config.Username.Should().Be("alice");
        config.Password.Should().Be("secret");
        config.LibraryPath.Should().Be("/config/xtream-library");
        config.DispatcharrApiUser.Should().Be("alice");
        config.DispatcharrApiPass.Should().Be("dispatcharr-pass");
        config.EnableDispatcharrMode.Should().BeTrue();
        config.SelectedVodCategoryIds.Should().BeEmpty();
        config.SelectedSeriesCategoryIds.Should().BeEmpty();
        config.Providers.Should().BeEmpty("the v1.31 schema predates the Providers collection");
    }

    [Fact]
    public void XmlRoundtrip_DefaultConfig_DeserializesWithoutError()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var original = new PluginConfiguration();

        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        using var reader = new StringReader(writer.ToString());
        var roundtripped = (PluginConfiguration)serializer.Deserialize(reader)!;

        roundtripped.Providers.Should().BeEmpty();
        roundtripped.SelectedLiveCategoryIds.Should().BeEmpty();
        roundtripped.BaseUrl.Should().BeEmpty();
    }

    // =====================
    // LiveChannelMode migration
    // =====================

    [Fact]
    public void ShouldMigrateToCustomMode_PreOverhaulConfigWithSelectedCategories_Migrates()
    {
        // Existing user upgraded from pre-v1.35: LiveChannelMode defaults to IncludeAll
        // (the field didn't exist before), but SelectedLiveCategoryIds is populated.
        // Must promote to Custom to preserve their selection behavior.
        var shouldMigrate = PluginConfiguration.ShouldMigrateToCustomMode(
            LiveChannelSelectionMode.IncludeAll,
            selectedCategoryCount: 3,
            excludedStreamCount: 0);

        shouldMigrate.Should().BeTrue();
    }

    [Fact]
    public void ShouldMigrateToCustomMode_PreOverhaulConfigWithExcludedStreamsOnly_Migrates()
    {
        // Some configs only have per-channel exclusions but no category selection — still
        // counts as user intent for selective sync, so migrate to Custom.
        var shouldMigrate = PluginConfiguration.ShouldMigrateToCustomMode(
            LiveChannelSelectionMode.IncludeAll,
            selectedCategoryCount: 0,
            excludedStreamCount: 17);

        shouldMigrate.Should().BeTrue();
    }

    [Fact]
    public void ShouldMigrateToCustomMode_FreshConfigWithNoState_DoesNotMigrate()
    {
        // New install: no state, default IncludeAll. Leave it alone.
        var shouldMigrate = PluginConfiguration.ShouldMigrateToCustomMode(
            LiveChannelSelectionMode.IncludeAll,
            selectedCategoryCount: 0,
            excludedStreamCount: 0);

        shouldMigrate.Should().BeFalse();
    }

    [Fact]
    public void ShouldMigrateToCustomMode_AlreadyCustomMode_DoesNotMigrate()
    {
        // Idempotency: once migrated, the guard skips on subsequent startups regardless of
        // selection state changes.
        PluginConfiguration.ShouldMigrateToCustomMode(LiveChannelSelectionMode.Custom, 0, 0).Should().BeFalse();
        PluginConfiguration.ShouldMigrateToCustomMode(LiveChannelSelectionMode.Custom, 5, 10).Should().BeFalse();
    }

    [Fact]
    public void LiveChannelMode_DefaultIsIncludeAll()
    {
        var config = new PluginConfiguration();
        config.LiveChannelMode.Should().Be(LiveChannelSelectionMode.IncludeAll);
    }

    [Fact]
    public void LiveChannelMode_RoundTripsThroughXmlSerialization()
    {
        var original = new PluginConfiguration { LiveChannelMode = LiveChannelSelectionMode.Custom };
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        using var reader = new StringReader(writer.ToString());
        var roundtripped = (PluginConfiguration)serializer.Deserialize(reader)!;

        roundtripped.LiveChannelMode.Should().Be(LiveChannelSelectionMode.Custom);
    }

    [Fact]
    public void UseBetaChannel_DefaultsToFalse()
    {
        var config = new PluginConfiguration();
        config.UseBetaChannel.Should().BeFalse();
    }

    [Fact]
    public void UseBetaChannel_XmlRoundtrip_PreservesTrueValue()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var original = new PluginConfiguration { UseBetaChannel = true };

        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        using var reader = new StringReader(writer.ToString());
        var roundtripped = (PluginConfiguration)serializer.Deserialize(reader)!;

        roundtripped.UseBetaChannel.Should().BeTrue();
    }
}
