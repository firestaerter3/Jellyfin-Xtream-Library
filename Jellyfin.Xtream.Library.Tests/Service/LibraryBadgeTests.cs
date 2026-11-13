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
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Data.Enums;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public sealed class LibraryBadgeTests : IDisposable
{
    private const string Library = "/config/xtream-library";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "badge-tests-" + Guid.NewGuid().ToString("N"));

    public LibraryBadgeTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private static PluginConfiguration Config(bool enabled, string text = "IPTV", string libraryPath = Library, string corner = "BottomLeft")
    {
        var config = new PluginConfiguration { EnableLibraryBadge = enabled, LibraryBadgeText = text, LibraryBadgeCorner = corner };
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://p1.example", LibraryPath = libraryPath });
        return config;
    }

    // ---- which images get a badge ----

    [Fact]
    public void TextIsNullWhenTheBadgeIsOff()
    {
        LibraryBadge.TextFor(Config(enabled: false)).Should().BeNull();
        LibraryBadge.TextFor(null).Should().BeNull();
    }

    [Fact]
    public void TextIsTrimmedAndCut()
    {
        LibraryBadge.TextFor(Config(true, "  IPTV ")).Should().Be("IPTV");
        LibraryBadge.TextFor(Config(true, "   ")).Should().BeNull();
        LibraryBadge.TextFor(Config(true, "ABCDEFGHIJKLMNOP")).Should().Be("ABCDEFGHIJKL");
    }

    [Theory]
    [InlineData(BaseItemKind.Movie, Library + "/Movies/Film (2020)/Film (2020).strm", ImageType.Primary, true)]
    [InlineData(BaseItemKind.Movie, Library + "/Movies/Action/Film (2020)/Film (2020).strm", ImageType.Primary, true)]
    [InlineData(BaseItemKind.Series, Library + "/Series/Show (2019)", ImageType.Primary, true)]
    [InlineData(BaseItemKind.Movie, Library + "/Movies/Film (2020)/Film (2020).strm", ImageType.Backdrop, false)]
    [InlineData(BaseItemKind.Season, Library + "/Series/Show (2019)/Season 1", ImageType.Primary, true)]
    [InlineData(BaseItemKind.Episode, Library + "/Series/Show (2019)/Season 1/Show S01E01.strm", ImageType.Primary, true)]
    [InlineData(BaseItemKind.BoxSet, Library + "/Movies/Collection", ImageType.Primary, false)]
    [InlineData(BaseItemKind.Episode, "/media/tv/Show/Season 1/Show S01E01.mkv", ImageType.Primary, false)]
    [InlineData(BaseItemKind.Movie, "/media/movies/Film (2020)/Film (2020).mkv", ImageType.Primary, false)]
    [InlineData(BaseItemKind.Movie, Library + "/MoviesExtra/Film (2020)/Film (2020).strm", ImageType.Primary, false)]
    [InlineData(BaseItemKind.Movie, Library + "/Movies", ImageType.Primary, false)]
    [InlineData(BaseItemKind.Movie, null, ImageType.Primary, false)]
    public void AppliesOnlyToPrimaryImagesOfTheFourTypesInThePluginFolders(BaseItemKind kind, string? path, ImageType type, bool expected)
    {
        var roots = LibraryBadge.LibraryRoots(Config(true));

        LibraryBadge.AppliesTo(kind, path, type, roots).Should().Be(expected);
    }

    [Fact]
    public void LibraryPathWithTrailingSlashStillMatches()
    {
        var roots = LibraryBadge.LibraryRoots(Config(true, libraryPath: Library + "/"));

        LibraryBadge.AppliesTo(BaseItemKind.Movie, Library + "/Movies/Film/Film.strm", ImageType.Primary, roots).Should().BeTrue();
    }

    [Fact]
    public void CombinedTagDiffersAndFollowsEveryBadgeSetting()
    {
        string tag = "0123456789abcdef0123456789abcdef";
        var style = new LibraryBadgeStyle("IPTV", LibraryBadgeCorner.BottomRight, "MSNE");

        string iptv = LibraryBadge.CombineTag(tag, style);

        iptv.Should().NotBe(tag).And.HaveLength(32).And.MatchRegex("^[0-9a-f]{32}$");
        LibraryBadge.CombineTag(tag, style).Should().Be(iptv);
        LibraryBadge.CombineTag(tag, style with { Text = "VOD" }).Should().NotBe(iptv);
        LibraryBadge.CombineTag(tag, style with { Corner = LibraryBadgeCorner.TopLeft }).Should().NotBe(iptv);
        LibraryBadge.CombineTag(tag, style with { Kinds = "MS" }).Should().NotBe(iptv);
        LibraryBadge.CombineTag("ffffffffffffffffffffffffffffffff", style).Should().NotBe(iptv);
    }

    [Fact]
    public void DefaultsAreBottomRightOnAllFourTypes()
    {
        var config = new PluginConfiguration();

        config.EnableLibraryBadge.Should().BeFalse();
        LibraryBadge.CornerFor(config).Should().Be(LibraryBadgeCorner.BottomRight);
        config.EnableLibraryBadge = true;
        LibraryBadge.StyleFor(config).Should().Be(new LibraryBadgeStyle("IPTV", LibraryBadgeCorner.BottomRight, "MSNE"));
    }

    [Theory]
    [InlineData("TopLeft", "TopLeft")]
    [InlineData("topright", "TopRight")]
    [InlineData("BottomLeft", "BottomLeft")]
    [InlineData("BottomRight", "BottomRight")]
    [InlineData("", "BottomRight")]
    [InlineData(null, "BottomRight")]
    [InlineData("Middle", "BottomRight")]
    [InlineData("0", "BottomRight")]
    [InlineData("7", "BottomRight")]
    [InlineData("TopLeft,TopRight", "BottomRight")]
    public void CornerFallsBackToBottomRight(string? value, string expected)
    {
        var config = Config(true);
        config.LibraryBadgeCorner = value!;

        LibraryBadge.CornerFor(config).ToString().Should().Be(expected);
    }

    [Fact]
    public void StyleListsOnlyTheTypesSwitchedOnAndIsNullWithNone()
    {
        var config = Config(true);
        config.LibraryBadgeOnSeries = false;
        config.LibraryBadgeOnEpisodes = false;

        LibraryBadge.StyleFor(config)!.Value.Kinds.Should().Be("MN");
        LibraryBadge.KindEnabled(config, BaseItemKind.Series).Should().BeFalse();
        LibraryBadge.KindEnabled(config, BaseItemKind.BoxSet).Should().BeFalse();

        config.LibraryBadgeOnMovies = false;
        config.LibraryBadgeOnSeasons = false;
        LibraryBadge.StyleFor(config).Should().BeNull();
    }

    // ---- the wrapper ----

    private string Poster(string name = "poster.jpg", int width = 200, int height = 300)
    {
        string path = Path.Combine(_dir, name);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(0x20, 0x60, 0xC0));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private (LibraryBadgeImageProcessor Processor, Mock<IImageProcessor> Inner) Wrapper(string resultPath, Func<PluginConfiguration?> config)
    {
        var inner = new Mock<IImageProcessor>();
        inner.Setup(p => p.ProcessImage(It.IsAny<ImageProcessingOptions>()))
            .ReturnsAsync((resultPath, "image/jpeg", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        inner.Setup(p => p.GetImageCacheTag(It.IsAny<BaseItem>(), It.IsAny<ItemImageInfo>())).Returns("0123456789abcdef0123456789abcdef");
        var processor = new LibraryBadgeImageProcessor(inner.Object, Path.Combine(_dir, "cache"), config, NullLogger.Instance);
        return (processor, inner);
    }

    private static ImageProcessingOptions Options(BaseItem item, ImageType type = ImageType.Primary)
        => new() { Item = item, Image = new ItemImageInfo { Type = type, Path = "/posters/poster.jpg" } };

    private static Movie IptvMovie() => new() { Path = Library + "/Movies/Film (2020)/Film (2020).strm" };

    private static SKColor PixelAt(string path, Func<SKBitmap, (int X, int Y)> where)
    {
        using var bitmap = SKBitmap.Decode(path);
        var (x, y) = where(bitmap);
        return bitmap.GetPixel(x, y);
    }

    [Fact]
    public async Task BadgeOffServesTheServersImage()
    {
        string poster = Poster();
        var (processor, _) = Wrapper(poster, () => Config(enabled: false));

        var result = await processor.ProcessImage(Options(IptvMovie()));

        result.Path.Should().Be(poster);
    }

    [Fact]
    public async Task BadgeOnDrawsACopyBottomLeftAndLeavesTheFileAlone()
    {
        string poster = Poster();
        byte[] before = SHA256.HashData(File.ReadAllBytes(poster));
        var (processor, _) = Wrapper(poster, () => Config(enabled: true));

        var result = await processor.ProcessImage(Options(IptvMovie()));

        result.Path.Should().NotBe(poster).And.StartWith(Path.Combine(_dir, "cache"));
        result.MimeType.Should().Be("image/jpeg");
        File.Exists(result.Path).Should().BeTrue();
        SHA256.HashData(File.ReadAllBytes(poster)).Should().Equal(before);

        // Bottom-left is now dark, top-right is still the poster's blue.
        var bottomLeft = PixelAt(result.Path, b => ((int)(b.Width * 0.06), (int)(b.Height * 0.94)));
        var topRight = PixelAt(result.Path, b => (b.Width - 5, 5));
        bottomLeft.Blue.Should().BeLessThan(100);
        topRight.Blue.Should().BeGreaterThan(150);
    }

    [Fact]
    public async Task ASecondRequestReusesTheCachedCopy()
    {
        string poster = Poster();
        var (processor, _) = Wrapper(poster, () => Config(enabled: true));

        var first = await processor.ProcessImage(Options(IptvMovie()));
        var written = File.GetLastWriteTimeUtc(first.Path);
        var second = await processor.ProcessImage(Options(IptvMovie()));

        second.Path.Should().Be(first.Path);
        File.GetLastWriteTimeUtc(second.Path).Should().Be(written);
        Directory.GetFiles(Path.Combine(_dir, "cache"), "*", SearchOption.AllDirectories).Should().HaveCount(1);
    }

    [Fact]
    public async Task OtherLibrariesAndBackdropsAreNotBadged()
    {
        string poster = Poster();
        var (processor, _) = Wrapper(poster, () => Config(enabled: true));

        (await processor.ProcessImage(Options(new Movie { Path = "/media/movies/Film/Film.mkv" }))).Path.Should().Be(poster);
        (await processor.ProcessImage(Options(IptvMovie(), ImageType.Backdrop))).Path.Should().Be(poster);
        (await processor.ProcessImage(Options(new Series { Path = Library + "/Series/Show" }))).Path.Should().NotBe(poster);
        (await processor.ProcessImage(Options(new Season { Path = Library + "/Series/Show/Season 1" }))).Path.Should().NotBe(poster);
        (await processor.ProcessImage(Options(new Episode { Path = Library + "/Series/Show/Season 1/Show S01E01.strm" }))).Path.Should().NotBe(poster);
    }

    [Theory]
    [InlineData("Movies")]
    [InlineData("Series")]
    [InlineData("Seasons")]
    [InlineData("Episodes")]
    public async Task ATypeSwitchedOffIsServedPlainWithTheServersTag(string off)
    {
        string poster = Poster();
        var (processor, _) = Wrapper(poster, () =>
        {
            var config = Config(enabled: true);
            config.LibraryBadgeOnMovies = off != "Movies";
            config.LibraryBadgeOnSeries = off != "Series";
            config.LibraryBadgeOnSeasons = off != "Seasons";
            config.LibraryBadgeOnEpisodes = off != "Episodes";
            return config;
        });
        var items = new (string Type, BaseItem Item)[]
        {
            ("Movies", IptvMovie()),
            ("Series", new Series { Path = Library + "/Series/Show" }),
            ("Seasons", new Season { Path = Library + "/Series/Show/Season 1" }),
            ("Episodes", new Episode { Path = Library + "/Series/Show/Season 1/Show S01E01.strm" }),
        };
        var primary = new ItemImageInfo { Type = ImageType.Primary, Path = "/posters/poster.jpg" };

        foreach (var (type, item) in items)
        {
            bool badged = type != off;
            ((await processor.ProcessImage(Options(item))).Path != poster).Should().Be(badged, type);
            (processor.GetImageCacheTag(item, primary) != "0123456789abcdef0123456789abcdef").Should().Be(badged, type);
        }
    }

    [Theory]
    [InlineData("TopLeft")]
    [InlineData("TopRight")]
    [InlineData("BottomLeft")]
    [InlineData("BottomRight")]
    public async Task TheBadgeIsDrawnInTheChosenCorner(string corner)
    {
        string poster = Poster();
        var (processor, _) = Wrapper(poster, () => Config(enabled: true, corner: corner));

        var result = await processor.ProcessImage(Options(IptvMovie()));

        var corners = new (string Name, Func<SKBitmap, (int X, int Y)> Where)[]
        {
            ("TopLeft", b => ((int)(b.Width * 0.06), (int)(b.Height * 0.06))),
            ("TopRight", b => ((int)(b.Width * 0.94), (int)(b.Height * 0.06))),
            ("BottomLeft", b => ((int)(b.Width * 0.06), (int)(b.Height * 0.94))),
            ("BottomRight", b => ((int)(b.Width * 0.94), (int)(b.Height * 0.94))),
        };
        foreach (var (name, where) in corners)
        {
            var pixel = PixelAt(result.Path, where);
            if (name == corner)
            {
                pixel.Blue.Should().BeLessThan(100, name);
            }
            else
            {
                pixel.Blue.Should().BeGreaterThan(150, name);
            }
        }
    }

    [Fact]
    public async Task ChangingTheCornerDrawsANewCopy()
    {
        string poster = Poster();
        string corner = "BottomLeft";
        var (processor, _) = Wrapper(poster, () => Config(enabled: true, corner: corner));

        var first = await processor.ProcessImage(Options(IptvMovie()));
        corner = "TopRight";
        var second = await processor.ProcessImage(Options(IptvMovie()));

        second.Path.Should().NotBe(first.Path);
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 4, 2, "left")]
    [InlineData(SKEncodedOrigin.TopRight, 4, 2, "right")]
    [InlineData(SKEncodedOrigin.BottomRight, 4, 2, "right")]
    [InlineData(SKEncodedOrigin.BottomLeft, 4, 2, "left")]
    [InlineData(SKEncodedOrigin.LeftTop, 2, 4, "top")]
    [InlineData(SKEncodedOrigin.RightTop, 2, 4, "top")]
    [InlineData(SKEncodedOrigin.RightBottom, 2, 4, "bottom")]
    [InlineData(SKEncodedOrigin.LeftBottom, 2, 4, "bottom")]
    public void OrientTurnsTheImageUprightAsExifSays(SKEncodedOrigin origin, int width, int height, string redSide)
    {
        // 4x2 stored image: left half red, right half blue.
        using var stored = new SKBitmap(4, 2);
        for (int x = 0; x < 4; x++)
        {
            for (int y = 0; y < 2; y++)
            {
                stored.SetPixel(x, y, x < 2 ? SKColors.Red : SKColors.Blue);
            }
        }

        using var upright = LibraryBadgeRenderer.Orient(stored, origin);

        upright.Width.Should().Be(width);
        upright.Height.Should().Be(height);
        var (rx, ry) = redSide switch
        {
            "left" => (0, 0),
            "right" => (width - 1, 0),
            "top" => (0, 0),
            _ => (0, height - 1),
        };
        var (bx, by) = redSide switch
        {
            "left" => (width - 1, 0),
            "right" => (0, 0),
            "top" => (0, height - 1),
            _ => (0, 0),
        };
        upright.GetPixel(rx, ry).Red.Should().BeGreaterThan(200, "red side " + redSide);
        upright.GetPixel(bx, by).Blue.Should().BeGreaterThan(200, "blue opposite " + redSide);
    }

    [Fact]
    public void OnALandscapeImageTheBadgeIsSizedToTheShorterSide()
    {
        using var portrait = new SKBitmap(200, 300);
        using var landscape = new SKBitmap(300, 200);
        using var portraitCanvas = new SKCanvas(portrait);
        using var landscapeCanvas = new SKCanvas(landscape);

        var onPoster = LibraryBadgeRenderer.DrawBadge(portraitCanvas, 200, 300, "IPTV", LibraryBadgeCorner.BottomRight);
        var onThumb = LibraryBadgeRenderer.DrawBadge(landscapeCanvas, 300, 200, "IPTV", LibraryBadgeCorner.BottomRight);

        onThumb.Height.Should().BeApproximately(onPoster.Height, 0.01f);
        onThumb.Width.Should().BeApproximately(onPoster.Width, 0.01f);
        onThumb.Right.Should().BeApproximately(300 - (200 * 0.04f), 0.01f);
        onThumb.Bottom.Should().BeApproximately(200 - (200 * 0.04f), 0.01f);
    }

    [Fact]
    public async Task AnUndecodableImageServesTheServersImage()
    {
        string broken = Path.Combine(_dir, "broken.jpg");
        File.WriteAllText(broken, "not an image");
        var (processor, _) = Wrapper(broken, () => Config(enabled: true));

        var result = await processor.ProcessImage(Options(IptvMovie()));

        result.Path.Should().Be(broken);
    }

    [Fact]
    public async Task AFormatItDoesNotDrawOnServesTheServersImage()
    {
        string gif = Path.Combine(_dir, "poster.gif");
        File.WriteAllBytes(gif, new byte[] { 0x47, 0x49, 0x46 });
        var (processor, _) = Wrapper(gif, () => Config(enabled: true));

        (await processor.ProcessImage(Options(IptvMovie()))).Path.Should().Be(gif);
    }

    [Fact]
    public async Task AFailingConfigurationServesThePlainImageAndKeepsTheTag()
    {
        string poster = Poster();
        var (processor, _) = Wrapper(poster, () => throw new InvalidCastException("boom"));
        var movie = IptvMovie();

        (await processor.ProcessImage(Options(movie))).Path.Should().Be(poster);
        processor.GetImageCacheTag(movie, new ItemImageInfo { Type = ImageType.Primary, Path = "/posters/poster.jpg" }).Should().Be("0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public void TagChangesOnlyForBadgedPostersAndFollowsTheSwitch()
    {
        var enabled = true;
        var (processor, _) = Wrapper(Poster(), () => Config(enabled));
        var primary = new ItemImageInfo { Type = ImageType.Primary, Path = "/posters/poster.jpg" };
        const string Plain = "0123456789abcdef0123456789abcdef";

        string on = processor.GetImageCacheTag(IptvMovie(), primary);
        on.Should().NotBe(Plain);
        processor.GetImageCacheTag(new Movie { Path = "/media/movies/Film/Film.mkv" }, primary).Should().Be(Plain);
        processor.GetImageCacheTag(IptvMovie(), new ItemImageInfo { Type = ImageType.Backdrop, Path = "/posters/poster.jpg" }).Should().Be(Plain);

        enabled = false;
        processor.GetImageCacheTag(IptvMovie(), primary).Should().Be(Plain);
    }

    [Fact]
    public void DtoTagUsesTheSameRule()
    {
        var inner = new Mock<IImageProcessor>();
        inner.Setup(p => p.GetImageCacheTag(It.IsAny<MediaBrowser.Model.Dto.BaseItemDto>(), It.IsAny<ItemImageInfo>())).Returns("0123456789abcdef0123456789abcdef");
        var processor = new LibraryBadgeImageProcessor(inner.Object, _dir, () => Config(true), NullLogger.Instance);
        var primary = new ItemImageInfo { Type = ImageType.Primary, Path = "/posters/poster.jpg" };

        processor.GetImageCacheTag(new MediaBrowser.Model.Dto.BaseItemDto { Type = BaseItemKind.Series, Path = Library + "/Series/Show" }, primary)
            .Should().Be(LibraryBadge.CombineTag("0123456789abcdef0123456789abcdef", new LibraryBadgeStyle("IPTV", LibraryBadgeCorner.BottomLeft, "MSNE")));
        processor.GetImageCacheTag(new MediaBrowser.Model.Dto.BaseItemDto { Type = BaseItemKind.Series }, primary)
            .Should().Be("0123456789abcdef0123456789abcdef");
    }

    [Theory]
    [InlineData("https://image.example/poster.jpg")]
    [InlineData("/posters/poster.gif")]
    [InlineData("/posters/poster.svg")]
    [InlineData("poster.jpg")]
    public async Task AnImageItCannotDrawOnKeepsTheTagAndIsServedPlain(string imagePath)
    {
        string poster = Poster();
        var (processor, _) = Wrapper(poster, () => Config(enabled: true));
        var image = new ItemImageInfo { Type = ImageType.Primary, Path = imagePath };

        processor.GetImageCacheTag(IptvMovie(), image).Should().Be("0123456789abcdef0123456789abcdef");
        (await processor.ProcessImage(new ImageProcessingOptions { Item = IptvMovie(), Image = image })).Path.Should().Be(poster);
    }

    [Theory]
    [InlineData("/posters/poster.jpg", true)]
    [InlineData("/posters/poster.JPEG", true)]
    [InlineData("/posters/poster.png", true)]
    [InlineData("/posters/poster.webp", true)]
    [InlineData("/posters/poster.gif", false)]
    [InlineData("http://image.example/poster.jpg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DrawableLocalImage(string? path, bool expected)
    {
        LibraryBadge.IsDrawableLocalImage(path).Should().Be(expected);
    }

    [Fact]
    public void AnInstanceRegisteredProcessorIsNotDisposedByTheWrapper()
    {
        var inner = new Mock<IImageProcessor>();
        var disposable = inner.As<IDisposable>();
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.ImageCachePath).Returns(_dir);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(paths.Object);
        services.AddSingleton(inner.Object);
        LibraryBadgeImageProcessor.Decorate(services);

        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<IImageProcessor>();
        }

        disposable.Verify(d => d.Dispose(), Times.Never);
    }

    // ---- registration ----

    [Fact]
    public void DecorateWrapsTheRegisteredProcessor()
    {
        var inner = new Mock<IImageProcessor>();
        inner.Setup(p => p.SupportsImageCollageCreation).Returns(true);
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.ImageCachePath).Returns(_dir);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(paths.Object);
        services.AddSingleton(inner.Object);

        LibraryBadgeImageProcessor.Decorate(services).Should().BeTrue();

        services.Count(d => d.ServiceType == typeof(IImageProcessor)).Should().Be(1);
        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IImageProcessor>();
        resolved.Should().BeOfType<LibraryBadgeImageProcessor>();
        resolved.SupportsImageCollageCreation.Should().BeTrue();
        provider.GetRequiredService<IImageProcessor>().Should().BeSameAs(resolved);
    }

    [Fact]
    public void DecorateBuildsATypeRegisteredProcessor()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.ImageCachePath).Returns(_dir);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(paths.Object);
        services.AddSingleton<IImageProcessor, FakeProcessor>();

        LibraryBadgeImageProcessor.Decorate(services).Should().BeTrue();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IImageProcessor>().SupportedInputFormats.Should().Equal("fake");
    }

    [Fact]
    public void DecorateDoesNothingWithoutAProcessor()
    {
        var services = new ServiceCollection();

        LibraryBadgeImageProcessor.Decorate(services).Should().BeFalse();
        services.Should().BeEmpty();
    }

    private sealed class FakeProcessor : IImageProcessor
    {
        public System.Collections.Generic.IReadOnlyCollection<string> SupportedInputFormats { get; } = new[] { "fake" };

        public bool SupportsImageCollageCreation => false;

        public MediaBrowser.Model.Drawing.ImageDimensions GetImageDimensions(string path) => default;

        public MediaBrowser.Model.Drawing.ImageDimensions GetImageDimensions(BaseItem item, ItemImageInfo info) => default;

        public string GetImageBlurHash(string path) => string.Empty;

        public string GetImageBlurHash(string path, MediaBrowser.Model.Drawing.ImageDimensions imageDimensions) => string.Empty;

        public string? GetImageCacheTag(string baseItemPath, DateTime imageDateModified) => null;

        public string? GetImageCacheTag(MediaBrowser.Model.Dto.BaseItemDto item, ChapterInfo image) => null;

        public string GetImageCacheTag(BaseItem item, ItemImageInfo image) => string.Empty;

        public string GetImageCacheTag(MediaBrowser.Model.Dto.BaseItemDto item, ItemImageInfo image) => string.Empty;

        public string? GetImageCacheTag(BaseItem item, ChapterInfo chapter) => null;

        public string? GetImageCacheTag(Jellyfin.Database.Implementations.Entities.User user) => null;

        public Task<(string Path, string? MimeType, DateTime DateModified)> ProcessImage(ImageProcessingOptions options)
            => Task.FromResult((string.Empty, (string?)null, DateTime.MinValue));

        public System.Collections.Generic.IReadOnlyCollection<MediaBrowser.Model.Drawing.ImageFormat> GetSupportedImageOutputFormats()
            => Array.Empty<MediaBrowser.Model.Drawing.ImageFormat>();

        public void CreateImageCollage(ImageCollageOptions options, string? libraryName)
        {
        }
    }
}
