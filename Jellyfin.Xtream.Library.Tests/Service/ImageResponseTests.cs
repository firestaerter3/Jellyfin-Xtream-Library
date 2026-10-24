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

using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class ImageResponseTests
{
    private static HttpResponseMessage Response(byte[] body, string? mediaType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = mediaType is null ? null : new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public async Task KeepsImagesAndUntypedBodies(string? mediaType)
    {
        using var response = Response(new byte[] { 1, 2, 3 }, mediaType);

        var bytes = await StrmSyncService.ReadImageResponseAsync(response, CancellationToken.None);

        bytes.Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    public async Task RefusesPagesThatAreNotImages(string mediaType)
    {
        using var response = Response(new byte[] { 1, 2, 3 }, mediaType);

        (await StrmSyncService.ReadImageResponseAsync(response, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RefusesABodyLargerThanTheLimit()
    {
        // The address comes from the provider; without a limit one answer could fill the server's memory.
        using var response = Response(new byte[StrmSyncService.MaxImageBytes + 1], "image/jpeg");

        (await StrmSyncService.ReadImageResponseAsync(response, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RefusesAnEmptyBody()
    {
        using var response = Response(System.Array.Empty<byte>(), "image/jpeg");

        (await StrmSyncService.ReadImageResponseAsync(response, CancellationToken.None)).Should().BeNull();
    }
}
