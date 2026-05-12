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
}
