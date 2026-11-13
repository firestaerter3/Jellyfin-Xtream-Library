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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Wraps the server's image processor to draw the library badge on the posters of the plugin's
/// movies and series while they are served. Image files on disk are never changed: the badged copy
/// lives in the image cache, and switching the badge off serves the plain poster again.
/// </summary>
/// <remarks>
/// Every image the server serves passes through here, so everything that is not a badged poster
/// goes straight to the inner processor, and any failure while drawing serves the plain poster.
/// </remarks>
internal sealed class LibraryBadgeImageProcessor : IImageProcessor, IDisposable
{
    private readonly IImageProcessor _inner;
    private readonly LibraryBadgeRenderer _renderer;
    private readonly Func<PluginConfiguration?> _config;
    private readonly ILogger _logger;
    private readonly bool _ownsInner;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryBadgeImageProcessor"/> class.
    /// </summary>
    /// <param name="inner">The server's image processor.</param>
    /// <param name="cacheDirectory">Folder for the badged copies.</param>
    /// <param name="config">Returns the current plugin configuration, or null.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="ownsInner">Whether this wrapper created <paramref name="inner"/> and so disposes it.</param>
    public LibraryBadgeImageProcessor(IImageProcessor inner, string cacheDirectory, Func<PluginConfiguration?> config, ILogger logger, bool ownsInner = false)
    {
        _inner = inner;
        _ownsInner = ownsInner;
        _renderer = new LibraryBadgeRenderer(cacheDirectory);
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedInputFormats => _inner.SupportedInputFormats;

    /// <inheritdoc />
    public bool SupportsImageCollageCreation => _inner.SupportsImageCollageCreation;

    /// <summary>
    /// Replaces the registered <see cref="IImageProcessor"/> with this wrapper around it. Plugins
    /// register their services after the server's own, so the server's processor is already in
    /// <paramref name="services"/> when this runs. Does nothing when no processor is registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>True when the processor was wrapped.</returns>
    public static bool Decorate(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IImageProcessor) && !d.IsKeyedService);
        if (descriptor is null)
        {
            return false;
        }

        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(
            typeof(IImageProcessor),
            sp => new LibraryBadgeImageProcessor(
                CreateInner(sp, descriptor),
                Path.Combine(sp.GetRequiredService<IApplicationPaths>().ImageCachePath, "xtream-library-badge"),
                CurrentConfiguration,
                sp.GetRequiredService<ILogger<LibraryBadgeImageProcessor>>(),
                ownsInner: descriptor.ImplementationInstance is null),
            descriptor.Lifetime));
        return true;
    }

    /// <inheritdoc />
    public async Task<(string Path, string? MimeType, DateTime DateModified)> ProcessImage(ImageProcessingOptions options)
    {
        var result = await _inner.ProcessImage(options).ConfigureAwait(false);

        string? text;
        try
        {
            text = options.Item is null || options.Image is null
                ? null
                : BadgeTextFor(options.Item.GetBaseItemKind(), options.Item.Path, options.Image);
        }
#pragma warning disable CA1031 // A failure here must serve the plain image, never break image serving.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Library badge: could not decide whether {Path} gets a badge", result.Path);
            return result;
        }

        if (text is null)
        {
            return result;
        }

        try
        {
            string? badged = await _renderer.GetOrCreateAsync(result.Path, result.DateModified, text).ConfigureAwait(false);
            if (badged is null)
            {
                return result;
            }

            return (badged, result.MimeType, File.GetLastWriteTimeUtc(badged));
        }
#pragma warning disable CA1031 // A failure here must serve the plain image, never break image serving.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Library badge: drawing failed for {Path}; serving the plain image", result.Path);
            return result;
        }
    }

    /// <inheritdoc />
    public string GetImageCacheTag(BaseItem item, ItemImageInfo image)
    {
        string tag = _inner.GetImageCacheTag(item, image);
        string? text = SafeBadgeText(() => BadgeTextFor(item.GetBaseItemKind(), item.Path, image));
        return text is null ? tag : LibraryBadge.CombineTag(tag, text);
    }

    /// <inheritdoc />
    public string GetImageCacheTag(BaseItemDto item, ItemImageInfo image)
    {
        string tag = _inner.GetImageCacheTag(item, image);
        string? text = SafeBadgeText(() => BadgeTextFor(item.Type, item.Path, image));
        return text is null ? tag : LibraryBadge.CombineTag(tag, text);
    }

    /// <inheritdoc />
    public string? GetImageCacheTag(string baseItemPath, DateTime imageDateModified)
        => _inner.GetImageCacheTag(baseItemPath, imageDateModified);

    /// <inheritdoc />
    public string? GetImageCacheTag(BaseItemDto item, ChapterInfo image) => _inner.GetImageCacheTag(item, image);

    /// <inheritdoc />
    public string? GetImageCacheTag(BaseItem item, ChapterInfo chapter) => _inner.GetImageCacheTag(item, chapter);

    /// <inheritdoc />
    public string? GetImageCacheTag(User user) => _inner.GetImageCacheTag(user);

    /// <inheritdoc />
    public ImageDimensions GetImageDimensions(string path) => _inner.GetImageDimensions(path);

    /// <inheritdoc />
    public ImageDimensions GetImageDimensions(BaseItem item, ItemImageInfo info) => _inner.GetImageDimensions(item, info);

    /// <inheritdoc />
    public string GetImageBlurHash(string path) => _inner.GetImageBlurHash(path);

    /// <inheritdoc />
    public string GetImageBlurHash(string path, ImageDimensions imageDimensions) => _inner.GetImageBlurHash(path, imageDimensions);

    /// <inheritdoc />
    public IReadOnlyCollection<ImageFormat> GetSupportedImageOutputFormats() => _inner.GetSupportedImageOutputFormats();

    /// <inheritdoc />
    public void CreateImageCollage(ImageCollageOptions options, string? libraryName) => _inner.CreateImageCollage(options, libraryName);

    /// <inheritdoc />
    public void Dispose()
    {
        _renderer.Dispose();
        if (_ownsInner)
        {
            (_inner as IDisposable)?.Dispose();
        }
    }

    private static IImageProcessor CreateInner(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IImageProcessor instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (IImageProcessor)descriptor.ImplementationFactory(sp);
        }

        return (IImageProcessor)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
    }

    private static PluginConfiguration? CurrentConfiguration()
    {
        try
        {
            return Plugin.Instance.Configuration;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the badge text for an image, or null when it gets no badge. The tag and the served
    /// image both come from this one rule, so a client never gets a badged tag for an image that is
    /// served plain by design. Only a local JPEG, PNG or WebP poster qualifies: those are the files
    /// the renderer draws on, and the server's processed copy of one is again one of those formats.
    /// </summary>
    /// <param name="kind">The item's kind.</param>
    /// <param name="itemPath">The item's path.</param>
    /// <param name="image">The image.</param>
    /// <returns>The badge text, or null.</returns>
    private string? BadgeTextFor(BaseItemKind kind, string? itemPath, ItemImageInfo image)
    {
        ImageType imageType = image.Type;
        if (imageType != ImageType.Primary || (kind != BaseItemKind.Movie && kind != BaseItemKind.Series) ||
            !LibraryBadge.IsDrawableLocalImage(image.Path))
        {
            return null;
        }

        var config = _config();
        string? text = LibraryBadge.TextFor(config);
        if (text is null || config is null)
        {
            return null;
        }

        return LibraryBadge.AppliesTo(kind, itemPath, imageType, LibraryBadge.LibraryRoots(config)) ? text : null;
    }

    private string? SafeBadgeText(Func<string?> decide)
    {
        try
        {
            return decide();
        }
#pragma warning disable CA1031 // A failure here must keep the server's own tag.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Library badge: could not decide on an image tag; keeping the server's own");
            return null;
        }
    }
}
