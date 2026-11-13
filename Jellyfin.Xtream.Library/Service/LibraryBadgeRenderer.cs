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
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Draws the library badge on a copy of an image and keeps that copy in a cache folder. The
/// source image is never changed.
/// </summary>
internal sealed class LibraryBadgeRenderer : IDisposable
{
    // Kept for the life of the process and never disposed: SKTypeface.Default is shared.
    private static readonly Lazy<SKTypeface> BoldTypeface = new(
        () => SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default);

    private readonly string _cacheDirectory;

    // Decoding and encoding a poster takes memory; the server limits its own encodings the same way.
    private readonly SemaphoreSlim _gate = new(2, 2);

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryBadgeRenderer"/> class.
    /// </summary>
    /// <param name="cacheDirectory">Folder for the badged copies.</param>
    public LibraryBadgeRenderer(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>
    /// Returns the path of a badged copy of <paramref name="sourcePath"/>, drawing it first when
    /// it is not cached yet. The copy has the same format as the source.
    /// </summary>
    /// <param name="sourcePath">The image to copy; the server's processed image or the original.</param>
    /// <param name="sourceModified">The source's modification date, part of the cache key.</param>
    /// <param name="text">The badge text.</param>
    /// <returns>The copy's path, or null when the format is not one this draws on or the file cannot be decoded.</returns>
    public async Task<string?> GetOrCreateAsync(string sourcePath, DateTime sourceModified, string text)
    {
        string extension = Path.GetExtension(sourcePath);
        SKEncodedImageFormat? format = FormatFor(extension);
        if (format is null || !File.Exists(sourcePath))
        {
            return null;
        }

        string target = CachePathFor(_cacheDirectory, sourcePath, sourceModified, text, extension);
        if (File.Exists(target))
        {
            return target;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }

            byte[]? encoded = Render(sourcePath, text, format.Value);
            if (encoded is null)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".part-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            try
            {
                await File.WriteAllBytesAsync(temporary, encoded).ConfigureAwait(false);
                try
                {
                    File.Move(temporary, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Another request drew the same copy first; it is identical.
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Returns the encoder format for a file extension, or null for one the badge is not drawn on.
    /// </summary>
    /// <param name="extension">Extension including the dot.</param>
    /// <returns>The format, or null.</returns>
    internal static SKEncodedImageFormat? FormatFor(string extension)
    {
        if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return SKEncodedImageFormat.Jpeg;
        }

        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            return SKEncodedImageFormat.Png;
        }

        if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
        {
            return SKEncodedImageFormat.Webp;
        }

        return null;
    }

    /// <summary>
    /// Returns where the badged copy of an image is kept. The key covers the source path, its date,
    /// the text and the drawing version, so a changed source or text gets a new copy.
    /// </summary>
    /// <param name="cacheDirectory">The cache folder.</param>
    /// <param name="sourcePath">The source image.</param>
    /// <param name="sourceModified">The source's modification date.</param>
    /// <param name="text">The badge text.</param>
    /// <param name="extension">Extension of the copy, including the dot.</param>
    /// <returns>The cache path.</returns>
    internal static string CachePathFor(string cacheDirectory, string sourcePath, DateTime sourceModified, string text, string extension)
    {
        string input = string.Create(
            CultureInfo.InvariantCulture,
            $"{sourcePath}|{sourceModified.Ticks}|{text}|{LibraryBadge.DrawingVersion}");
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 16));
        return Path.Combine(cacheDirectory, key[..2], key + extension.ToLowerInvariant());
    }

    /// <summary>
    /// Decodes an image, draws the badge on it and encodes it again.
    /// </summary>
    /// <param name="sourcePath">The image.</param>
    /// <param name="text">The badge text.</param>
    /// <param name="format">Output format.</param>
    /// <returns>The encoded image, or null when it cannot be decoded.</returns>
    internal static byte[]? Render(string sourcePath, string text, SKEncodedImageFormat format)
    {
        using var bitmap = SKBitmap.Decode(sourcePath);
        if (bitmap is null || bitmap.Width < 1 || bitmap.Height < 1)
        {
            return null;
        }

        using (var canvas = new SKCanvas(bitmap))
        {
            DrawBadge(canvas, bitmap.Width, bitmap.Height, text);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, format == SKEncodedImageFormat.Png ? 100 : 90);
        return data?.ToArray();
    }

    /// <summary>
    /// Draws the badge in the bottom-left corner: white bold text on a dark rounded box, sized
    /// relative to the image so it looks the same on every poster size.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="text">The badge text.</param>
    /// <returns>The box drawn, for tests.</returns>
    internal static SKRect DrawBadge(SKCanvas canvas, int width, int height, string text)
    {
        float shortSide = Math.Min(width, height);
        float boxHeight = Math.Max(10f, shortSide * 0.11f);
        float margin = shortSide * 0.04f;
        float padding = boxHeight * 0.35f;

        using var font = new SKFont(BoldTypeface.Value, boxHeight * 0.62f);
        font.Embolden = true;
        float textWidth = font.MeasureText(text);

        float boxWidth = Math.Min(textWidth + (2 * padding), width - (2 * margin));
        var box = SKRect.Create(margin, height - margin - boxHeight, boxWidth, boxHeight);

        using (var fill = new SKPaint { Color = new SKColor(0x10, 0x10, 0x10, 0xD9), IsAntialias = true })
        {
            canvas.DrawRoundRect(box, boxHeight * 0.22f, boxHeight * 0.22f, fill);
        }

        using (var ink = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            font.GetFontMetrics(out var metrics);
            float baseline = box.MidY - ((metrics.Ascent + metrics.Descent) / 2f);
            canvas.DrawText(text, box.MidX, baseline, SKTextAlign.Center, font, ink);
        }

        return box;
    }
}
