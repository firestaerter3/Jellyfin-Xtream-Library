using System;
using System.IO;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>Tests persistent artwork download failure tracking.</summary>
public class ArtworkFailureTrackerTests
{
    /// <summary>Three failures block an image, survive reload, and success clears the block.</summary>
    [Fact]
    public void ThreeFailuresPersistAndSuccessClears()
    {
        var root = Path.Combine(Path.GetTempPath(), "xtream-artwork-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hash = ArtworkFailureTracker.Hash("https://example.invalid/bad.jpg");
            var other = ArtworkFailureTracker.Hash("https://example.invalid/good.jpg");
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance);
            tracker.Record(hash, false);
            tracker.Record(hash, false);
            Assert.False(tracker.IsBlocked(hash));
            tracker.Record(hash, false);
            Assert.True(tracker.IsBlocked(hash));
            Assert.False(tracker.IsBlocked(other));
            var reloaded = new ArtworkFailureTracker(root, NullLogger.Instance);
            Assert.True(reloaded.IsBlocked(hash));
            reloaded.Record(hash, true);
            Assert.False(new ArtworkFailureTracker(root, NullLogger.Instance).IsBlocked(hash));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
    [Fact]
    public void CustomPolicyApplies()
    {
        var root = Path.Combine(Path.GetTempPath(), "artcfg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hash = ArtworkFailureTracker.Hash("https://example.invalid/artcfg");
            var tracker = new ArtworkFailureTracker(root, NullLogger.Instance, () => 2, () => 1);
            tracker.Record(hash, false);
            Assert.False(tracker.IsBlocked(hash));
            tracker.Record(hash, false);
            Assert.True(tracker.IsBlocked(hash));
            Assert.True(new ArtworkFailureTracker(root, NullLogger.Instance, () => 2, () => 1).IsBlocked(hash));
            var file = Path.Combine(root, "xtream-library", "artwork_failures.jsonl");
            var entry = Newtonsoft.Json.JsonConvert.DeserializeObject<ArtworkFailureTracker.Entry>(File.ReadAllLines(file)[1]);
            Assert.NotNull(entry);
            Assert.InRange(entry.RetryAfterUtc, DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(25));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

}
