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

#pragma warning disable CS0618 // BUG-009 tests intentionally exercise the legacy single-provider fields
using System;
using System.IO;
using FluentAssertions;
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests;

// BUG-009: orphan legacy config file import on startup. See BUGS.md.
[Collection("PluginSingletonTests")]
public class PluginTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IApplicationPaths> _appPaths;

    // The PluginConfiguration filename Jellyfin's BasePlugin<T> default resolves to in tests.
    // We intentionally pick a name that is NOT in OrphanCandidateFileNames so we can drop
    // orphan files into the same dir without the active config grabbing them.
    private const string ActiveConfigFileName = "Jellyfin.Xtream.Library.test-active.xml";

    public PluginTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "claude", "plugin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _appPaths = new Mock<IApplicationPaths>();
        _appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_tempDir);
        _appPaths.Setup(p => p.DataPath).Returns(_tempDir);
        _appPaths.Setup(p => p.ProgramDataPath).Returns(_tempDir);
        _appPaths.Setup(p => p.CachePath).Returns(_tempDir);
        _appPaths.Setup(p => p.LogDirectoryPath).Returns(_tempDir);
        _appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(_tempDir);
        _appPaths.Setup(p => p.TempDirectory).Returns(_tempDir);
        _appPaths.Setup(p => p.PluginsPath).Returns(_tempDir);
        _appPaths.Setup(p => p.WebPath).Returns(_tempDir);
        _appPaths.Setup(p => p.ProgramSystemPath).Returns(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup. A locked file in the temp dir shouldn't fail the test run.
        }

        GC.SuppressFinalize(this);
    }

    private void WriteOrphan(string fileName, string xml)
    {
        File.WriteAllText(Path.Combine(_tempDir, fileName), xml);
    }

    private static string LegacyOrphanXml(string baseUrl = "http://provider.example:8080", string movieMappings = "Action=42")
        => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <BaseUrl>{baseUrl}</BaseUrl>
  <Username>alice</Username>
  <Password>secret</Password>
  <LibraryPath>/config/xtream-library</LibraryPath>
  <SyncMovies>true</SyncMovies>
  <SyncSeries>true</SyncSeries>
  <SelectedVodCategoryIds />
  <SelectedSeriesCategoryIds />
  <MovieFolderMode>Multiple</MovieFolderMode>
  <MovieFolderMappings>{movieMappings}</MovieFolderMappings>
  <SeriesFolderMode>Multiple</SeriesFolderMode>
  <SeriesFolderMappings>Drama=99</SeriesFolderMappings>
</PluginConfiguration>";

    // Pretends to be Kevin Jilissen's upstream Jellyfin.Xtream plugin config: shares the
    // <PluginConfiguration> root element and overlapping field names (BaseUrl etc.) but has
    // none of our schema's distinguishing fields (folder mappings, override maps).
    private const string UpstreamShapedXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<PluginConfiguration xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <BaseUrl>http://upstream-plugin.example</BaseUrl>
  <Username>bob</Username>
  <Password>other-plugin-pass</Password>
</PluginConfiguration>";

    [Fact]
    public void Ctor_NoOrphanExists_NoOp()
    {
        var serializer = new RealXmlSerializer();

        var plugin = new Plugin(_appPaths.Object, serializer);

        plugin.Configuration.Providers.Should().BeEmpty();
        plugin.Configuration.BaseUrl.Should().BeNullOrEmpty();
        plugin.Configuration.MovieFolderMappings.Should().BeNullOrEmpty();
        Directory.GetFiles(_tempDir).Should().NotContain(p => p.EndsWith(".bak", StringComparison.Ordinal));
    }

    [Fact]
    public void Ctor_OrphanExists_FreshConfig_ImportsAndRenamesOrphan()
    {
        // Drop the orphan at "Jellyfin.Xtream.xml" so it does NOT collide with whatever
        // ConfigurationFileName Jellyfin's BasePlugin defaults to in this test. The realistic
        // user scenario is that the active config is at one filename and the orphan with the
        // user's data is at the other — that's the case we're verifying.
        WriteOrphan("Jellyfin.Xtream.xml", LegacyOrphanXml());
        var serializer = new RealXmlSerializer();

        var plugin = new Plugin(_appPaths.Object, serializer);

        // MigrateProvidersIfNeeded ran after the import, so Providers[0] is populated.
        plugin.Configuration.Providers.Should().HaveCount(1);
        plugin.Configuration.Providers[0].BaseUrl.Should().Be("http://provider.example:8080");
        plugin.Configuration.Providers[0].Username.Should().Be("alice");
        plugin.Configuration.Providers[0].MovieFolderMappings.Should().Be("Action=42");
        plugin.Configuration.Providers[0].SeriesFolderMappings.Should().Be("Drama=99");

        // Orphan was renamed to a .bak audit copy.
        File.Exists(Path.Combine(_tempDir, "Jellyfin.Xtream.xml")).Should().BeFalse();
        Directory.GetFiles(_tempDir, "Jellyfin.Xtream.xml.migrated-*.bak").Should().ContainSingle();
    }

    [Fact]
    public void Ctor_OrphanExists_ButActiveConfigHasProviders_SkipsImport()
    {
        // Active config already populated — even if an orphan exists, importing would be
        // worse than the current bug, so the trigger gate refuses.
        var activeConfig = new PluginConfiguration();
        activeConfig.Providers.Add(new ProviderConfig
        {
            Name = "Active",
            IsEnabled = true,
            BaseUrl = "http://already-here.example",
            Username = "existinguser",
        });

        WriteOrphan("Jellyfin.Xtream.xml", LegacyOrphanXml());

        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(activeConfig);

        var plugin = new Plugin(_appPaths.Object, serializer.Object);

        plugin.Configuration.Providers.Should().HaveCount(1);
        plugin.Configuration.Providers[0].BaseUrl.Should().Be("http://already-here.example");
        // Orphan file untouched on disk — no .bak created.
        File.Exists(Path.Combine(_tempDir, "Jellyfin.Xtream.xml")).Should().BeTrue();
        Directory.GetFiles(_tempDir, "*.bak").Should().BeEmpty();
    }

    [Fact]
    public void Ctor_OrphanExists_ButActiveConfigHasBaseUrl_SkipsImport()
    {
        // The existing MigrateProvidersIfNeeded covers the "BaseUrl populated, no Providers"
        // case directly. Orphan import should defer to that path and not interfere.
        var activeConfig = new PluginConfiguration
        {
            BaseUrl = "http://in-memory-active.example",
            Username = "in-memory-user",
        };

        WriteOrphan("Jellyfin.Xtream.xml", LegacyOrphanXml());

        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(activeConfig);

        var plugin = new Plugin(_appPaths.Object, serializer.Object);

        // The existing MigrateProvidersIfNeeded should have populated Providers[0] from the
        // active in-memory config — not from the orphan.
        plugin.Configuration.Providers.Should().HaveCount(1);
        plugin.Configuration.Providers[0].BaseUrl.Should().Be("http://in-memory-active.example");
        File.Exists(Path.Combine(_tempDir, "Jellyfin.Xtream.xml")).Should().BeTrue();
        Directory.GetFiles(_tempDir, "*.bak").Should().BeEmpty();
    }

    [Fact]
    public void Ctor_OrphanIsMalformedXml_LogsWarningAndContinues()
    {
        WriteOrphan("Jellyfin.Xtream.xml", "<<not valid xml>>");
        var serializer = new RealXmlSerializer();

        // Should not throw — the malformed orphan is caught, skipped, and the plugin loads
        // normally with default-empty config.
        var plugin = new Plugin(_appPaths.Object, serializer);

        plugin.Configuration.Providers.Should().BeEmpty();
        // Malformed orphan stays put on disk — we did not migrate from it.
        File.Exists(Path.Combine(_tempDir, "Jellyfin.Xtream.xml")).Should().BeTrue();
        Directory.GetFiles(_tempDir, "*.bak").Should().BeEmpty();
    }

    [Fact]
    public void Ctor_TwoValidOrphans_NewerMtimeWinsAndOnlyChosenIsRenamed()
    {
        // For this test we want BOTH orphan candidate filenames to actually be orphans
        // (i.e. neither matches the active config Jellyfin loaded). Jellyfin's default
        // ConfigurationFileName in this test resolves to "Jellyfin.Xtream.Library.xml",
        // so to force both candidates to be orphans we'd need to override that. Easier:
        // verify the multi-candidate logic by writing two valid candidates and asserting
        // that exactly one is renamed and the other is left in place. Since the active-
        // config-named candidate is silently excluded from the scan, only "Jellyfin.Xtream.xml"
        // is eligible — but if it has data, it's the only candidate, and the multi-candidate
        // tie-break never runs. Test the tie-break by making BOTH candidates non-default-named.
        // Workaround: use the test subclass below that overrides ConfigurationFileName.

        // Backdate the older file so the second write is unambiguously newer.
        WriteOrphan("Jellyfin.Xtream.Library.xml", LegacyOrphanXml(baseUrl: "http://older.example", movieMappings: "Older=1"));
        File.SetLastWriteTimeUtc(Path.Combine(_tempDir, "Jellyfin.Xtream.Library.xml"), DateTime.UtcNow.AddMinutes(-10));

        WriteOrphan("Jellyfin.Xtream.xml", LegacyOrphanXml(baseUrl: "http://newer.example", movieMappings: "Newer=2"));

        var serializer = new RealXmlSerializer();

        // The harness subclass changes ConfigurationFileName to something OUTSIDE the orphan
        // candidate list, so both candidate files are eligible orphans.
        var plugin = new PluginWithOverriddenActiveConfigName(_appPaths.Object, serializer);

        plugin.Configuration.Providers.Should().HaveCount(1);
        plugin.Configuration.Providers[0].BaseUrl.Should().Be("http://newer.example");
        plugin.Configuration.Providers[0].MovieFolderMappings.Should().Be("Newer=2");

        // Chosen orphan was renamed; loser was left untouched on disk.
        File.Exists(Path.Combine(_tempDir, "Jellyfin.Xtream.xml")).Should().BeFalse();
        File.Exists(Path.Combine(_tempDir, "Jellyfin.Xtream.Library.xml")).Should().BeTrue("the loser candidate is left for the user to inspect");
        Directory.GetFiles(_tempDir, "Jellyfin.Xtream.xml.migrated-*.bak").Should().ContainSingle();
    }

    // Test-only harness: Jellyfin's BasePlugin default ConfigurationFileName collides with
    // one of our orphan candidates in this test environment, which prevents us from testing
    // the multi-candidate path. Overriding ConfigurationFileName to something outside the
    // candidate list lets both real candidate filenames count as orphans.
    private sealed class PluginWithOverriddenActiveConfigName : Plugin
    {
        public PluginWithOverriddenActiveConfigName(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
        }

        public override string ConfigurationFileName => "Jellyfin.Xtream.Library.test-active.xml";
    }

    [Fact]
    public void Ctor_OrphanFailsSchemaSentinel_DoesNotImport()
    {
        // Mimics the upstream Jellyfin.Xtream plugin: has BaseUrl/Username but none of the
        // fields that distinguish our schema (folder mappings, TMDb/TVDB override maps).
        WriteOrphan("Jellyfin.Xtream.xml", UpstreamShapedXml);
        var serializer = new RealXmlSerializer();

        var plugin = new Plugin(_appPaths.Object, serializer);

        plugin.Configuration.Providers.Should().BeEmpty();
        plugin.Configuration.BaseUrl.Should().BeNullOrEmpty();
        // Upstream-shaped file stays put — we refuse to migrate from it.
        File.Exists(Path.Combine(_tempDir, "Jellyfin.Xtream.xml")).Should().BeTrue();
        Directory.GetFiles(_tempDir, "*.bak").Should().BeEmpty();
    }

    [Fact]
    public void Ctor_RenameFails_StillImportsAndLeavesOrphanInPlace()
    {
        WriteOrphan("Jellyfin.Xtream.xml", LegacyOrphanXml());

        // Pre-create the destination as a DIRECTORY so File.Move into it throws — exercises
        // the catch path. The import itself must still succeed in memory.
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var bakBlocker = Path.Combine(_tempDir, $"Jellyfin.Xtream.xml.migrated-{stamp}.bak");
        Directory.CreateDirectory(bakBlocker);

        var serializer = new RealXmlSerializer();

        var plugin = new Plugin(_appPaths.Object, serializer);

        // Import succeeded.
        plugin.Configuration.Providers.Should().HaveCount(1);
        plugin.Configuration.Providers[0].MovieFolderMappings.Should().Be("Action=42");

        // Rename failed, so the original orphan stayed on disk (user data not lost).
        File.Exists(Path.Combine(_tempDir, "Jellyfin.Xtream.xml")).Should().BeTrue();
    }
}
