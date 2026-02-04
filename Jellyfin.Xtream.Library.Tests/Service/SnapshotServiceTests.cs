using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// Tests for <see cref="SnapshotService"/>.
/// </summary>
public class SnapshotServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<IServerApplicationPaths> _appPathsMock;
    private readonly SnapshotService _service;

    public SnapshotServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "xtream-snapshot-tests-" + Guid.NewGuid());
        _appPathsMock = new Mock<IServerApplicationPaths>();
        _appPathsMock.Setup(p => p.DataPath).Returns(_tempDirectory);
        _service = new SnapshotService(_appPathsMock.Object, NullLogger<SnapshotService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        // Arrange
        var snapshot = CreateTestSnapshot();

        // Act
        await _service.SaveSnapshotAsync(snapshot, CancellationToken.None);
        var loaded = await _service.LoadLatestSnapshotAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Version, loaded.Version);
        Assert.Equal(snapshot.ProviderUrl, loaded.ProviderUrl);
        Assert.Equal(snapshot.Movies.Count, loaded.Movies.Count);
        Assert.Equal(snapshot.Series.Count, loaded.Series.Count);
        Assert.True(loaded.Metadata.IsComplete);
        Assert.Equal(100, loaded.Metadata.TotalMovies);
        Assert.Equal(50, loaded.Metadata.TotalSeries);
    }

    [Fact]
    public async Task LoadLatestSnapshot_NoFiles_ReturnsNull()
    {
        // Act
        var result = await _service.LoadLatestSnapshotAsync(CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadLatestSnapshot_IncompleteSnapshot_ReturnsNull()
    {
        // Arrange
        var snapshot = CreateTestSnapshot();
        snapshot.Metadata.IsComplete = false;

        // Act
        await _service.SaveSnapshotAsync(snapshot, CancellationToken.None);
        var loaded = await _service.LoadLatestSnapshotAsync(CancellationToken.None);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task CleanupOldSnapshots_KeepsLatest3()
    {
        // Arrange & Act - Create 5 snapshots (cleanup runs after each save)
        for (int i = 0; i < 5; i++)
        {
            var snapshot = CreateTestSnapshot();
            await _service.SaveSnapshotAsync(snapshot, CancellationToken.None);
            // Ensure different timestamps - cleanup is synchronous per save
            await Task.Delay(10); // Millisecond granularity in filename, so 10ms is sufficient
        }

        // Assert
        var snapshotDir = Path.Combine(_tempDirectory, "xtream-library", ".snapshots");
        var files = Directory.GetFiles(snapshotDir, "snapshot_*.json");
        Assert.Equal(3, files.Length); // Cleanup keeps exactly 3
    }

    [Fact]
    public void CalculateChecksum_SameContent_SameHash()
    {
        // Arrange
        var movie1 = new StreamInfo
        {
            StreamId = 1,
            Name = "Test Movie",
            StreamIcon = "http://example.com/poster1.jpg", // Different poster URL
            ContainerExtension = "mkv",
            CategoryId = 10
        };

        var movie2 = new StreamInfo
        {
            StreamId = 2, // Different ID
            Name = "Test Movie",
            StreamIcon = "http://example.com/poster2.jpg", // Different poster URL
            ContainerExtension = "mkv",
            CategoryId = 10
        };

        // Act
        var checksum1 = SnapshotService.CalculateChecksum(movie1);
        var checksum2 = SnapshotService.CalculateChecksum(movie2);

        // Assert
        Assert.Equal(checksum1, checksum2); // Same content = same hash (poster URL ignored)
        Assert.NotEmpty(checksum1);
    }

    [Fact]
    public void CalculateChecksum_DifferentContent_DifferentHash()
    {
        // Arrange
        var movie1 = new StreamInfo
        {
            StreamId = 1,
            Name = "Test Movie",
            StreamIcon = "http://example.com/poster.jpg",
            ContainerExtension = "mkv",
            CategoryId = 10
        };

        var movie2 = new StreamInfo
        {
            StreamId = 1,
            Name = "Test Movie 2", // Different name - this DOES change hash
            StreamIcon = "http://example.com/poster.jpg",
            ContainerExtension = "mkv",
            CategoryId = 10
        };

        // Act
        var checksum1 = SnapshotService.CalculateChecksum(movie1);
        var checksum2 = SnapshotService.CalculateChecksum(movie2);

        // Assert
        Assert.NotEqual(checksum1, checksum2); // Different name = different hash
    }

    [Fact]
    public async Task SaveSnapshot_ConcurrentCalls_UsesLocking()
    {
        // Arrange
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(async () =>
            {
                var snapshot = CreateTestSnapshot();
                await _service.SaveSnapshotAsync(snapshot, CancellationToken.None);
            }))
            .ToArray();

        // Act
        await Task.WhenAll(tasks);

        // Assert - All snapshots should be saved without corruption
        var loaded = await _service.LoadLatestSnapshotAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.True(loaded.Metadata.IsComplete);
    }

    [Fact]
    public async Task LoadSnapshot_CorruptedJson_ReturnsNull()
    {
        // Arrange
        var snapshotDir = Path.Combine(_tempDirectory, "xtream-library", ".snapshots");
        Directory.CreateDirectory(snapshotDir);

        var corruptedFile = Path.Combine(snapshotDir, "snapshot_20260101_120000.json");
        await File.WriteAllTextAsync(corruptedFile, "{ invalid json }");

        // Act
        var result = await _service.LoadLatestSnapshotAsync(CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ConfigFingerprint_SameConfig_SameHash()
    {
        var config1 = new PluginConfiguration
        {
            MovieFolderMode = "Multiple",
            SeriesFolderMode = "Single",
            SelectedVodCategoryIds = new[] { 3, 1, 2 },
            EnableMetadataLookup = true,
        };

        var config2 = new PluginConfiguration
        {
            MovieFolderMode = "Multiple",
            SeriesFolderMode = "Single",
            SelectedVodCategoryIds = new[] { 2, 3, 1 }, // Different order, same IDs
            EnableMetadataLookup = true,
        };

        var fp1 = SnapshotService.CalculateConfigFingerprint(config1);
        var fp2 = SnapshotService.CalculateConfigFingerprint(config2);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void ConfigFingerprint_DifferentFolderMode_DifferentHash()
    {
        var config1 = new PluginConfiguration { MovieFolderMode = "Single" };
        var config2 = new PluginConfiguration { MovieFolderMode = "Multiple" };

        var fp1 = SnapshotService.CalculateConfigFingerprint(config1);
        var fp2 = SnapshotService.CalculateConfigFingerprint(config2);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void ConfigFingerprint_DifferentCategories_DifferentHash()
    {
        var config1 = new PluginConfiguration { SelectedVodCategoryIds = new[] { 1, 2, 3 } };
        var config2 = new PluginConfiguration { SelectedVodCategoryIds = new[] { 1, 2, 4 } };

        var fp1 = SnapshotService.CalculateConfigFingerprint(config1);
        var fp2 = SnapshotService.CalculateConfigFingerprint(config2);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void ConfigFingerprint_MetadataLookupToggle_DifferentHash()
    {
        var config1 = new PluginConfiguration { EnableMetadataLookup = true };
        var config2 = new PluginConfiguration { EnableMetadataLookup = false };

        var fp1 = SnapshotService.CalculateConfigFingerprint(config1);
        var fp2 = SnapshotService.CalculateConfigFingerprint(config2);

        Assert.NotEqual(fp1, fp2);
    }

    private static ContentSnapshot CreateTestSnapshot()
    {
        var snapshot = new ContentSnapshot
        {
            Version = 1,
            ProviderUrl = "http://test.example.com",
            Metadata = new SnapshotMetadata
            {
                TotalMovies = 100,
                TotalSeries = 50,
                IsComplete = true,
                SnapshotDuration = TimeSpan.FromMinutes(2)
            }
        };

        // Add sample movies
        for (int i = 1; i <= 100; i++)
        {
            snapshot.Movies[i] = new MovieSnapshot
            {
                StreamId = i,
                Name = $"Movie {i}",
                StreamIcon = $"http://example.com/movie{i}.jpg",
                ContainerExtension = "mkv",
                CategoryId = i % 10,
                Checksum = Guid.NewGuid().ToString("N")
            };
        }

        // Add sample series
        for (int i = 1; i <= 50; i++)
        {
            snapshot.Series[i] = new SeriesSnapshot
            {
                SeriesId = i,
                Name = $"Series {i}",
                Cover = $"http://example.com/series{i}.jpg",
                CategoryId = i % 5,
                EpisodeCount = i * 10,
                Checksum = Guid.NewGuid().ToString("N")
            };
        }

        return snapshot;
    }
}
