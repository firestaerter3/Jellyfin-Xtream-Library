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

using System.IO;
using System.Linq;
using FluentAssertions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests;

[Collection("PluginSingletonTests")]
public class PluginTests
{
    [Fact]
    public void GetPages_RegistersConfigPageInMainMenu()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "claude", "test-plugin-pages");
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempPath);
        applicationPaths.Setup(p => p.DataPath).Returns(tempPath);
        applicationPaths.Setup(p => p.ProgramDataPath).Returns(tempPath);
        applicationPaths.Setup(p => p.CachePath).Returns(tempPath);
        applicationPaths.Setup(p => p.LogDirectoryPath).Returns(tempPath);
        applicationPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tempPath);
        applicationPaths.Setup(p => p.TempDirectory).Returns(tempPath);
        applicationPaths.Setup(p => p.PluginsPath).Returns(tempPath);
        applicationPaths.Setup(p => p.WebPath).Returns(tempPath);
        applicationPaths.Setup(p => p.ProgramSystemPath).Returns(tempPath);

        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());

        var plugin = new Plugin(applicationPaths.Object, xmlSerializer.Object);

        var pages = plugin.GetPages().ToList();

        pages.Should().ContainSingle(p => p.Name == "config.html");
        pages.Should().ContainSingle(p => p.Name == "config.js");

        var configPage = pages.Single(p => p.Name == "config.html");
        configPage.EnableInMainMenu.Should().BeTrue();
        configPage.EmbeddedResourcePath.Should().Be("Jellyfin.Xtream.Library.Configuration.Web.config.html");
    }
}
