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
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public sealed class PortraitProgramImageResolverTests : IDisposable
{
    private const string Wide = "https://ebyl.tmsimg.com/assets/p29642504_b_h9_ad.jpg";
    private const string Portrait = "https://ebyl.tmsimg.com/assets/p29642504_b_v8_ad.jpg";
    private const string WideNoPoster = "https://ebyl.tmsimg.com/assets/p8651529_b_h9_bw.jpg";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "portrait-images-" + Guid.NewGuid().ToString("N"));

    private string CacheFile => Path.Combine(_dir, "cache.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string Guide(params string[] icons)
        => "<tv>" + string.Concat(icons.Select(i => $"<programme channel=\"a\"><title>x</title><icon src=\"{i}\" width=\"1440\" height=\"1080\" /></programme>")) + "</tv>";

    [Theory]
    [InlineData("https://ebyl.tmsimg.com/assets/p29642504_b_h9_ad.jpg", "https://ebyl.tmsimg.com/assets/p29642504_b_v8_ad.jpg")]
    [InlineData("https://ebyl.tmsimg.com/assets/p19262657_b_h6_af.jpg", "https://ebyl.tmsimg.com/assets/p19262657_b_v8_af.jpg")]
    [InlineData("https://ebyl.tmsimg.com/assets/p35634_v_v8_ad.jpg", null)]
    [InlineData("https://ebyl.tmsimg.com/assets/p35634_v_h9_ad.jpg", null)]
    [InlineData("https://example.com/assets/p1_b_h9_aa.jpg", null)]
    [InlineData("http://localhost:9191/api/channels/logos/1/cache/", null)]
    [InlineData("", null)]
    public void OnlyWideGracenoteImagesHaveACandidate(string url, string? expected)
        => PortraitProgramImageResolver.PortraitCandidate(url).Should().Be(expected);

    [Fact]
    public async Task AWideImageWithAPosterIsRewrittenAndOneWithoutIsKept()
    {
        using var r = new PortraitProgramImageResolver(CacheFile, NullLogger.Instance, (url, _) => Task.FromResult<bool?>(url == Portrait));
        await r.ResolveAsync(new[] { Guide(Wide, WideNoPoster) }, CancellationToken.None);

        r.Rewrite(Wide).Should().Be(Portrait);
        r.Rewrite(WideNoPoster).Should().Be(WideNoPoster);
    }

    [Fact]
    public async Task AnswersAreKeptOnDiskAndNotAskedAgain()
    {
        var calls = 0;
        using (var r = new PortraitProgramImageResolver(CacheFile, NullLogger.Instance, (url, _) => { Interlocked.Increment(ref calls); return Task.FromResult<bool?>(url == Portrait); }))
        {
            await r.ResolveAsync(new[] { Guide(Wide, WideNoPoster) }, CancellationToken.None);
        }

        calls.Should().Be(2);
        using var again = new PortraitProgramImageResolver(CacheFile, NullLogger.Instance, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<bool?>(false); });
        await again.ResolveAsync(new[] { Guide(Wide, WideNoPoster) }, CancellationToken.None);

        calls.Should().Be(2);
        again.Rewrite(Wide).Should().Be(Portrait);
    }

    [Fact]
    public async Task AFailedCheckIsNotRecordedAndTheImageIsKept()
    {
        var calls = 0;
        using var r = new PortraitProgramImageResolver(CacheFile, NullLogger.Instance, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("down");
        });
        await r.ResolveAsync(new[] { Guide(Wide) }, CancellationToken.None);
        r.Rewrite(Wide).Should().Be(Wide);

        await r.ResolveAsync(new[] { Guide(Wide) }, CancellationToken.None);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task AnUnknownAnswerIsNotRecorded()
    {
        using var r = new PortraitProgramImageResolver(CacheFile, NullLogger.Instance, (_, _) => Task.FromResult<bool?>(null));
        await r.ResolveAsync(new[] { Guide(Wide) }, CancellationToken.None);
        r.Rewrite(Wide).Should().Be(Wide);
        File.Exists(CacheFile).Should().BeTrue();
        File.ReadAllText(CacheFile).Should().NotContain("p29642504");
    }

    [Fact]
    public async Task OneRunChecksAtMostTheLimit()
    {
        var asked = new ConcurrentBag<string>();
        var icons = Enumerable.Range(1, PortraitProgramImageResolver.MaxLookupsPerRun + 50)
            .Select(i => $"https://ebyl.tmsimg.com/assets/p{i}_b_h9_aa.jpg")
            .ToArray();
        using var r = new PortraitProgramImageResolver(CacheFile, NullLogger.Instance, (url, _) => { asked.Add(url); return Task.FromResult<bool?>(false); });

        await r.ResolveAsync(new[] { Guide(icons) }, CancellationToken.None);
        asked.Should().HaveCount(PortraitProgramImageResolver.MaxLookupsPerRun);

        await r.ResolveAsync(new[] { Guide(icons) }, CancellationToken.None);
        asked.Should().HaveCount(PortraitProgramImageResolver.MaxLookupsPerRun + 50);
    }

    [Fact]
    public async Task AnUnreadableCacheFileIsIgnored()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(CacheFile, "{ not json");
        using var r = new PortraitProgramImageResolver(CacheFile, NullLogger.Instance, (url, _) => Task.FromResult<bool?>(url == Portrait));

        await r.ResolveAsync(new[] { Guide(Wide) }, CancellationToken.None);
        r.Rewrite(Wide).Should().Be(Portrait);
    }

    [Fact]
    public async Task UsePortraitIconsRewritesSrcAndDropsTheWideSize()
    {
        using var r = new PortraitProgramImageResolver(CacheFile, NullLogger.Instance, (url, _) => Task.FromResult<bool?>(url == Portrait));
        await r.ResolveAsync(new[] { Guide(Wide, WideNoPoster) }, CancellationToken.None);

        var withPoster = XElement.Parse($"<programme><title>x</title><icon src=\"{Wide}\" width=\"1440\" height=\"1080\" /></programme>");
        LiveTvService.UsePortraitIcons(withPoster, r);
        var icon = withPoster.Element("icon")!;
        icon.Attribute("src")!.Value.Should().Be(Portrait);
        icon.Attribute("width").Should().BeNull();
        icon.Attribute("height").Should().BeNull();

        var without = XElement.Parse($"<programme><title>x</title><icon src=\"{WideNoPoster}\" width=\"1440\" /></programme>");
        LiveTvService.UsePortraitIcons(without, r);
        without.Element("icon")!.Attribute("src")!.Value.Should().Be(WideNoPoster);
        without.Element("icon")!.Attribute("width")!.Value.Should().Be("1440");
    }
}
