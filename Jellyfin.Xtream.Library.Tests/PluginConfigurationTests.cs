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
    [Fact]
    public void Validate_ClampsSyncParallelism_ToValidRange()
    {
        var config = new PluginConfiguration
        {
            SyncParallelism = 50  // Exceeds max of 20
        };

        config.Validate();

        config.SyncParallelism.Should().Be(20);
    }

    [Fact]
    public void Validate_ClampsSyncParallelism_ToMinimumOne()
    {
        var config = new PluginConfiguration
        {
            SyncParallelism = 0  // Below minimum of 1
        };

        config.Validate();

        config.SyncParallelism.Should().Be(1);
    }

    [Fact]
    public void Validate_ClampsOrphanSafetyThreshold_ToValidRange()
    {
        var config1 = new PluginConfiguration
        {
            OrphanSafetyThreshold = 1.5  // Above 1.0
        };
        var config2 = new PluginConfiguration
        {
            OrphanSafetyThreshold = -0.1  // Below 0.0
        };

        config1.Validate();
        config2.Validate();

        config1.OrphanSafetyThreshold.Should().Be(1.0);
        config2.OrphanSafetyThreshold.Should().Be(0.0);
    }

    [Fact]
    public void Validate_ClampsRequestDelayMs_ToNonNegative()
    {
        var config = new PluginConfiguration
        {
            RequestDelayMs = -100  // Negative value
        };

        config.Validate();

        config.RequestDelayMs.Should().Be(0);
    }

    [Fact]
    public void Validate_ClampsMetadataParallelism_ToValidRange()
    {
        var config = new PluginConfiguration
        {
            MetadataParallelism = 15  // Exceeds max of 10
        };

        config.Validate();

        config.MetadataParallelism.Should().Be(10);
    }

    [Fact]
    public void Validate_ClampsCategoryBatchSize_ToValidRange()
    {
        var config = new PluginConfiguration
        {
            CategoryBatchSize = 150  // Exceeds max of 100
        };

        config.Validate();

        config.CategoryBatchSize.Should().Be(100);
    }

    [Fact]
    public void Validate_ClampsDailySchedule_ToValidTimeRange()
    {
        var config = new PluginConfiguration
        {
            SyncDailyHour = 25,  // Above 23
            SyncDailyMinute = 70  // Above 59
        };

        config.Validate();

        config.SyncDailyHour.Should().Be(23);
        config.SyncDailyMinute.Should().Be(59);
    }

    [Fact]
    public void Validate_ClampsFullSyncIntervalDays_ToValidRange()
    {
        var configLow = new PluginConfiguration { FullSyncIntervalDays = 0 };
        var configHigh = new PluginConfiguration { FullSyncIntervalDays = 60 };

        configLow.Validate();
        configHigh.Validate();

        configLow.FullSyncIntervalDays.Should().Be(1);
        configHigh.FullSyncIntervalDays.Should().Be(30);
    }

    [Fact]
    public void Validate_ClampsFullSyncChangeThreshold_ToValidRange()
    {
        var configLow = new PluginConfiguration { FullSyncChangeThreshold = -0.5 };
        var configHigh = new PluginConfiguration { FullSyncChangeThreshold = 2.0 };

        configLow.Validate();
        configHigh.Validate();

        configLow.FullSyncChangeThreshold.Should().Be(0.0);
        configHigh.FullSyncChangeThreshold.Should().Be(1.0);
    }

    [Fact]
    public void DefaultValues_AreReasonable()
    {
        var config = new PluginConfiguration();

        config.Validate();

        config.SyncParallelism.Should().Be(6);
        config.MetadataParallelism.Should().Be(3);
        config.OrphanSafetyThreshold.Should().Be(0.20);
        config.RequestDelayMs.Should().Be(100);
        config.MaxRetries.Should().Be(3);
        config.RetryDelayMs.Should().Be(1000);
        config.EnableIncrementalSync.Should().BeTrue();
        config.FullSyncIntervalDays.Should().Be(7);
        config.FullSyncChangeThreshold.Should().Be(0.50);
    }
}
