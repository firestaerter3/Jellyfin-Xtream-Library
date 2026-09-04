// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #84. A provider password saved with a trailing space was stored verbatim and interpolated
// straight into every stream URL, producing ".../<password> /12345.mkv" and a 403 on every item.
// The configuration page now trims the field, but that only helps a user who re-saves; trimming in
// ConnectionInfo is what repairs configurations that already carry the whitespace, because every
// caller that talks to the provider builds one of these first.

using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Client;

public class ConnectionInfoTests
{
    [Theory]
    [InlineData("secret ")]
    [InlineData(" secret")]
    [InlineData("  secret  ")]
    [InlineData("secret\t")]
    [InlineData("secret\n")]
    [InlineData("secret\u00A0")] // non-breaking space, the usual copy-paste artefact
    public void Password_IsTrimmed(string stored)
    {
        new ConnectionInfo("http://host:8901", "user", stored).Password.Should().Be("secret");
    }

    [Fact]
    public void BaseUrlAndUserName_AreTrimmed()
    {
        var info = new ConnectionInfo("  http://host:8901  ", " user ", "secret");

        info.BaseUrl.Should().Be("http://host:8901");
        info.UserName.Should().Be("user");
    }

    [Fact]
    public void InteriorWhitespace_IsPreserved()
    {
        new ConnectionInfo("http://host:8901", "first last", " pass word ").Password.Should().Be("pass word");
    }

    [Fact]
    public void WhitespaceOnlyValue_BecomesEmpty()
    {
        new ConnectionInfo("http://host:8901", "user", "   ").Password.Should().BeEmpty();
    }

    [Fact]
    public void NullValue_BecomesEmpty()
    {
        // The XML configuration can deserialize an absent element to null, and the old code stored
        // it as-is. Trimming must not turn that into a NullReferenceException mid-sync.
        var info = new ConnectionInfo(null!, null!, null!);

        info.BaseUrl.Should().BeEmpty();
        info.UserName.Should().BeEmpty();
        info.Password.Should().BeEmpty();
    }

    [Fact]
    public void StreamUrl_HasNoStrayWhitespace()
    {
        // The exact shape the reporter saw: StrmSyncService interpolates these three values.
        var info = new ConnectionInfo("http://host:8901", "user ", "secret ");

        var url = $"{info.BaseUrl}/movie/{info.UserName}/{info.Password}/572499.mkv";

        url.Should().Be("http://host:8901/movie/user/secret/572499.mkv");
        url.Should().NotContain(" ");
    }
}
