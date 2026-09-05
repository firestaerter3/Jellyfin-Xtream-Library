// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #83. Dispatcharr mode had no URL of its own, so the JWT login was posted to the Xtream
// endpoint. On the reporter's setup that endpoint has no /api/accounts/token/ route and answers
// 404, which surfaced as "Dispatcharr JWT login failed with status NotFound".

using FluentAssertions;
using Jellyfin.Xtream.Library;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests;

public class DispatcharrBaseUrlTests
{
    [Fact]
    public void EmptyDispatcharrUrl_FallsBackToTheXtreamUrl()
    {
        // Every install that worked before this field existed had them on one host, so an empty
        // value has to keep meaning exactly that.
        var provider = new ProviderConfig { BaseUrl = "http://same-host:8901" };

        provider.EffectiveDispatcharrBaseUrl.Should().Be("http://same-host:8901");
    }

    [Fact]
    public void ADedicatedUrl_IsUsedInsteadOfTheXtreamOne()
    {
        var provider = new ProviderConfig
        {
            BaseUrl = "http://xtream-host:8901",
            DispatcharrBaseUrl = "http://dispatcharr-host:9191",
        };

        provider.EffectiveDispatcharrBaseUrl.Should().Be("http://dispatcharr-host:9191");
    }

    [Theory]
    [InlineData("http://dispatcharr:9191/", "http://dispatcharr:9191")]
    [InlineData("  http://dispatcharr:9191  ", "http://dispatcharr:9191")]
    [InlineData("http://proxy/dispatcharr/", "http://proxy/dispatcharr")]
    public void TheUrlIsNormalised_BecauseEveryCallerAppendsApiPaths(string configured, string expected)
    {
        new ProviderConfig { BaseUrl = "http://xtream:8901", DispatcharrBaseUrl = configured }
            .EffectiveDispatcharrBaseUrl.Should().Be(expected);
    }

    [Fact]
    public void APathIsKept_ForDispatcharrBehindAReverseProxy()
    {
        new ProviderConfig { BaseUrl = "http://xtream:8901", DispatcharrBaseUrl = "https://proxy/dispatcharr" }
            .EffectiveDispatcharrBaseUrl.Should().Be("https://proxy/dispatcharr");
    }

    [Fact]
    public void WhitespaceOnly_CountsAsUnset()
    {
        new ProviderConfig { BaseUrl = "http://xtream:8901", DispatcharrBaseUrl = "   " }
            .EffectiveDispatcharrBaseUrl.Should().Be("http://xtream:8901");
    }
}
