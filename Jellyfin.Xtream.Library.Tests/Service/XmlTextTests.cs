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

using System.Xml.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class XmlTextTests
{
    [Fact]
    public void RemovesControlCharactersXmlCannotHold()
    {
        XmlText.RemoveInvalidChars("A\u0001B\u0008C\uFFFE").Should().Be("ABC");
    }

    [Fact]
    public void KeepsTabsNewlinesAndCharactersOutsideTheBasicPlane()
    {
        XmlText.RemoveInvalidChars("a\tb\nc \U0001F3AC").Should().Be("a\tb\nc \U0001F3AC");
    }

    [Fact]
    public void DropsALoneSurrogate()
    {
        XmlText.RemoveInvalidChars("a\uD83Cb").Should().Be("ab");
    }

    [Fact]
    public async System.Threading.Tasks.Task AnNfoWithAControlCharacterInTheTitleStillParses()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() + ".nfo");
        try
        {
            await NfoWriter.WriteMovieNfoAsync(path, "Bad\u0001Title", null, null, null, 603, null, System.Threading.CancellationToken.None);

            XDocument.Load(path).Root!.Element("title")!.Value.Should().Be("BadTitle");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
