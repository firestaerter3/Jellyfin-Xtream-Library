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
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Client;

public class XtreamClientTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<XtreamClient>> _mockLogger;
    private readonly XtreamClient _client;

    public XtreamClientTests()
    {
        _httpClient = new HttpClient();
        _mockLogger = new Mock<ILogger<XtreamClient>>();
        _client = new XtreamClient(_httpClient, _mockLogger.Object);
    }

    public void Dispose()
    {
        // HttpClient is managed by factory in production; dispose directly in tests
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    #region UpdateUserAgent Tests

    [Fact]
    public void UpdateUserAgent_NullAgent_SetsDefaultAgent()
    {
        _client.UpdateUserAgent(null);

        var userAgent = _httpClient.DefaultRequestHeaders.UserAgent.ToString();
        userAgent.Should().Contain("Jellyfin.Xtream.Library");
    }

    [Fact]
    public void UpdateUserAgent_EmptyString_SetsDefaultAgent()
    {
        _client.UpdateUserAgent(string.Empty);

        var userAgent = _httpClient.DefaultRequestHeaders.UserAgent.ToString();
        userAgent.Should().Contain("Jellyfin.Xtream.Library");
    }

    [Fact]
    public void UpdateUserAgent_WhitespaceString_SetsDefaultAgent()
    {
        _client.UpdateUserAgent("   ");

        var userAgent = _httpClient.DefaultRequestHeaders.UserAgent.ToString();
        userAgent.Should().Contain("Jellyfin.Xtream.Library");
    }

    [Fact]
    public void UpdateUserAgent_CustomString_SetsCustomAgent()
    {
        const string customAgent = "CustomPlayer/1.0";

        _client.UpdateUserAgent(customAgent);

        _httpClient.DefaultRequestHeaders.TryGetValues("User-Agent", out var values);
        values.Should().Contain(customAgent);
    }

    [Fact]
    public void UpdateUserAgent_CalledTwice_ClearsPreviousAgent()
    {
        _client.UpdateUserAgent("FirstAgent/1.0");
        _client.UpdateUserAgent("SecondAgent/1.0");

        _httpClient.DefaultRequestHeaders.TryGetValues("User-Agent", out var values);
        var agentList = values?.ToList();
        agentList.Should().NotBeNull();
        agentList!.Should().HaveCount(1);
        agentList.Should().Contain("SecondAgent/1.0");
    }

    #endregion

    #region ConnectionInfo Tests

    [Fact]
    public void ConnectionInfo_Constructor_SetsProperties()
    {
        var info = new ConnectionInfo("http://example.com", "user", "pass");

        info.BaseUrl.Should().Be("http://example.com");
        info.UserName.Should().Be("user");
        info.Password.Should().Be("pass");
    }

    [Fact]
    public void ConnectionInfo_ToString_FormatsCorrectly()
    {
        var info = new ConnectionInfo("http://example.com", "user", "pass");

        info.ToString().Should().Be("http://example.com user:pass");
    }

    [Fact]
    public void ConnectionInfo_PropertiesAreMutable()
    {
        var info = new ConnectionInfo("http://example.com", "user", "pass");

        info.BaseUrl = "http://new.com";
        info.UserName = "newuser";
        info.Password = "newpass";

        info.BaseUrl.Should().Be("http://new.com");
        info.UserName.Should().Be("newuser");
        info.Password.Should().Be("newpass");
    }

    #endregion
}
