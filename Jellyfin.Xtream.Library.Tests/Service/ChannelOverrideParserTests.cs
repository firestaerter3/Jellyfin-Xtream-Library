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
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class ChannelOverrideParserTests
{
    #region Parse Tests

    [Fact]
    public void Parse_ReturnsEmptyForNull()
    {
        var result = ChannelOverrideParser.Parse(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ReturnsEmptyForWhitespace()
    {
        var result = ChannelOverrideParser.Parse("   ");

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ParsesNameOnly()
    {
        var input = "123=BBC One";

        var result = ChannelOverrideParser.Parse(input);

        result.Should().ContainKey(123);
        result[123].StreamId.Should().Be(123);
        result[123].Name.Should().Be("BBC One");
        result[123].Number.Should().BeNull();
        result[123].LogoUrl.Should().BeNull();
    }

    [Fact]
    public void Parse_ParsesNameAndNumber()
    {
        var input = "456=CNN|2";

        var result = ChannelOverrideParser.Parse(input);

        result.Should().ContainKey(456);
        result[456].StreamId.Should().Be(456);
        result[456].Name.Should().Be("CNN");
        result[456].Number.Should().Be(2);
        result[456].LogoUrl.Should().BeNull();
    }

    [Fact]
    public void Parse_ParsesAllFields()
    {
        var input = "789=Sky News|5|http://example.com/logo.png";

        var result = ChannelOverrideParser.Parse(input);

        result.Should().ContainKey(789);
        result[789].StreamId.Should().Be(789);
        result[789].Name.Should().Be("Sky News");
        result[789].Number.Should().Be(5);
        result[789].LogoUrl.Should().Be("http://example.com/logo.png");
    }

    [Fact]
    public void Parse_ParsesNumberOnly()
    {
        var input = "101=|10|";

        var result = ChannelOverrideParser.Parse(input);

        result.Should().ContainKey(101);
        result[101].StreamId.Should().Be(101);
        result[101].Name.Should().BeNull();
        result[101].Number.Should().Be(10);
        result[101].LogoUrl.Should().BeNull();
    }

    [Fact]
    public void Parse_ParsesMultipleLines()
    {
        var input = "123=BBC One\n456=CNN|2\n789=Sky News|5|http://logo.png";

        var result = ChannelOverrideParser.Parse(input);

        result.Should().HaveCount(3);
        result.Should().ContainKey(123);
        result.Should().ContainKey(456);
        result.Should().ContainKey(789);
    }

    [Fact]
    public void Parse_IgnoresComments()
    {
        var input = "# This is a comment\n123=BBC One";

        var result = ChannelOverrideParser.Parse(input);

        result.Should().HaveCount(1);
        result.Should().ContainKey(123);
    }

    [Fact]
    public void Parse_IgnoresInvalidLines()
    {
        var input = "invalid line\n123=BBC One\n=NoId\nNoEquals";

        var result = ChannelOverrideParser.Parse(input);

        result.Should().HaveCount(1);
        result.Should().ContainKey(123);
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var input = "  123  =  BBC One  ";

        var result = ChannelOverrideParser.Parse(input);

        result.Should().ContainKey(123);
        result[123].Name.Should().Be("BBC One");
    }

    [Fact]
    public void Parse_HandlesWindowsLineEndings()
    {
        var input = "123=BBC One\r\n456=CNN";

        var result = ChannelOverrideParser.Parse(input);

        result.Should().HaveCount(2);
    }

    #endregion

    #region ApplyOverride Tests

    [Fact]
    public void ApplyOverride_DoesNothingForNullChannel()
    {
        var channelOverride = new ChannelOverride { StreamId = 123, Name = "Test" };

        // Should not throw
        ChannelOverrideParser.ApplyOverride(null!, channelOverride);
    }

    [Fact]
    public void ApplyOverride_DoesNothingForNullOverride()
    {
        var channel = new LiveStreamInfo { StreamId = 123, Name = "Original", Num = 1 };

        ChannelOverrideParser.ApplyOverride(channel, null);

        channel.Name.Should().Be("Original");
        channel.Num.Should().Be(1);
    }

    [Fact]
    public void ApplyOverride_AppliesNameOverride()
    {
        var channel = new LiveStreamInfo { StreamId = 123, Name = "Original", Num = 1 };
        var channelOverride = new ChannelOverride { StreamId = 123, Name = "New Name" };

        ChannelOverrideParser.ApplyOverride(channel, channelOverride);

        channel.Name.Should().Be("New Name");
        channel.Num.Should().Be(1); // Unchanged
    }

    [Fact]
    public void ApplyOverride_AppliesNumberOverride()
    {
        var channel = new LiveStreamInfo { StreamId = 123, Name = "Original", Num = 1 };
        var channelOverride = new ChannelOverride { StreamId = 123, Number = 99 };

        ChannelOverrideParser.ApplyOverride(channel, channelOverride);

        channel.Name.Should().Be("Original"); // Unchanged
        channel.Num.Should().Be(99);
    }

    [Fact]
    public void ApplyOverride_AppliesLogoOverride()
    {
        var channel = new LiveStreamInfo { StreamId = 123, Name = "Original", StreamIcon = "old.png" };
        var channelOverride = new ChannelOverride { StreamId = 123, LogoUrl = "new.png" };

        ChannelOverrideParser.ApplyOverride(channel, channelOverride);

        channel.StreamIcon.Should().Be("new.png");
    }

    [Fact]
    public void ApplyOverride_AppliesAllOverrides()
    {
        var channel = new LiveStreamInfo { StreamId = 123, Name = "Original", Num = 1, StreamIcon = "old.png" };
        var channelOverride = new ChannelOverride
        {
            StreamId = 123,
            Name = "New Name",
            Number = 99,
            LogoUrl = "new.png"
        };

        ChannelOverrideParser.ApplyOverride(channel, channelOverride);

        channel.Name.Should().Be("New Name");
        channel.Num.Should().Be(99);
        channel.StreamIcon.Should().Be("new.png");
    }

    [Fact]
    public void ApplyOverride_DoesNotOverrideWithEmptyName()
    {
        var channel = new LiveStreamInfo { StreamId = 123, Name = "Original" };
        var channelOverride = new ChannelOverride { StreamId = 123, Name = "" };

        ChannelOverrideParser.ApplyOverride(channel, channelOverride);

        channel.Name.Should().Be("Original");
    }

    #endregion
}
