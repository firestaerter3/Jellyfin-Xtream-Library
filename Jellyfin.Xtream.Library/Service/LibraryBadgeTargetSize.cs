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
using MediaBrowser.Controller.Drawing;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// The size a client asked for. Jellyfin never makes an image larger than its source, so a small
/// source comes back smaller than this and the client enlarges it, blurring the badge with it.
/// </summary>
/// <param name="Width">The requested width, or null.</param>
/// <param name="Height">The requested height, or null.</param>
/// <param name="Fill">True when the image must cover the size (fill), false when it must fit inside it.</param>
internal readonly record struct LibraryBadgeTargetSize(int? Width, int? Height, bool Fill)
{
    /// <summary>The largest factor an image is enlarged by.</summary>
    internal const float MaxScale = 4f;

    /// <summary>
    /// Returns the size a request asks for: fill size first, then maximum size, then exact size.
    /// </summary>
    /// <param name="options">The request's options.</param>
    /// <returns>The requested size; both sides null when the request names none.</returns>
    internal static LibraryBadgeTargetSize From(ImageProcessingOptions options)
    {
        if (options.FillWidth is > 0 || options.FillHeight is > 0)
        {
            return new(Positive(options.FillWidth), Positive(options.FillHeight), true);
        }

        if (options.MaxWidth is > 0 || options.MaxHeight is > 0)
        {
            return new(Positive(options.MaxWidth), Positive(options.MaxHeight), false);
        }

        return new(Positive(options.Width), Positive(options.Height), false);
    }

    /// <summary>
    /// Returns how much an image of the given size must be enlarged to reach this size, between 1
    /// (never smaller) and <see cref="MaxScale"/>.
    /// </summary>
    /// <param name="width">The image width.</param>
    /// <param name="height">The image height.</param>
    /// <returns>The scale factor.</returns>
    internal float ScaleFor(int width, int height)
    {
        if (width < 1 || height < 1)
        {
            return 1f;
        }

        float? sx = Width is int w ? (float)w / width : null;
        float? sy = Height is int h ? (float)h / height : null;
        float scale = (sx, sy) switch
        {
            (float x, float y) => Fill ? Math.Max(x, y) : Math.Min(x, y),
            (float x, null) => x,
            (null, float y) => y,
            _ => 1f,
        };

        // Rounding the requested size in the server can leave an image a pixel short; that is not
        // worth a new copy.
        return scale <= 1.02f ? 1f : Math.Min(scale, MaxScale);
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;
}
