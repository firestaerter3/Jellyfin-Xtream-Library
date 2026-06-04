# Beta Channel Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in "Use beta channel" checkbox to the plugin's General tab that registers the plugin's beta manifest URL with Jellyfin's plugin catalog, so users can receive pre-release versions through Dashboard → Plugins → Catalog without manually editing the repository list.

**Architecture:** A single `UseBetaChannel` bool on `PluginConfiguration` drives a new `BetaChannelManager` (`IHostedService`) that syncs the plugin's beta manifest URL in and out of `ServerConfiguration.PluginRepositories`. Sync runs at startup and on every `ConfigurationChanged` event. The decision logic is a pure static method for ease of testing.

**Tech Stack:** .NET 9, Jellyfin 10.11.0 plugin SDK, xUnit + FluentAssertions + Moq, ASP.NET Core hosted services.

**Reference:** See [design spec](../specs/2026-06-04-beta-channel-toggle-design.md) for rationale, alternatives considered, and end-to-end data flow diagrams.

---

## File Map

| File | Status | Responsibility |
|---|---|---|
| `Jellyfin.Xtream.Library/PluginConfiguration.cs` | Modify | Add `UseBetaChannel` bool (default `false`). |
| `Jellyfin.Xtream.Library/Service/BetaChannelManager.cs` | Create | `IHostedService` that mirrors `UseBetaChannel` into Jellyfin's `PluginRepositories`. |
| `Jellyfin.Xtream.Library/PluginServiceRegistrator.cs` | Modify | Register `BetaChannelManager` as a hosted service. |
| `Jellyfin.Xtream.Library/Configuration/Web/config.html` | Modify | New "Updates" sub-section in General tab with one checkbox. |
| `Jellyfin.Xtream.Library/Configuration/Web/config.js` | Modify | Bind checkbox to `UseBetaChannel` in load + save paths. |
| `Jellyfin.Xtream.Library.Tests/PluginConfigurationTests.cs` | Modify | Add default-value assertion. |
| `Jellyfin.Xtream.Library.Tests/Service/BetaChannelManagerTests.cs` | Create | Pure unit tests for `ComputeNextRepositories` + `StructurallyEqual`. |
| `Jellyfin.Xtream.Library.Tests/Service/BetaChannelManagerIntegrationTests.cs` | Create | Tests for `StartAsync` / `StopAsync` / event handling against a fake `IServerConfigurationManager`. |

---

## Task 1: Add `UseBetaChannel` to PluginConfiguration

**Goal:** Persist the user's beta-channel opt-in as a single `bool` on the existing plugin configuration, defaulting to `false`.

**Files:**
- Modify: `Jellyfin.Xtream.Library/PluginConfiguration.cs` (insert after the "Global: Schedule" section, around line 85)
- Modify: `Jellyfin.Xtream.Library.Tests/PluginConfigurationTests.cs` (append a new test)

**Acceptance Criteria:**
- [ ] `PluginConfiguration.UseBetaChannel` exists as a `bool` with default `false`.
- [ ] XML round-trip preserves the value.
- [ ] `dotnet build -c Release` succeeds with no new warnings.

**Verify:** `dotnet test -c Release --filter "FullyQualifiedName~PluginConfigurationTests"` → all green, including the new test.

**Steps:**

- [ ] **Step 1: Add the property to PluginConfiguration.cs**

Insert immediately after `SyncDailyMinute` (around line 85), as a new section:

```csharp
    // =====================
    // Global: Updates
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether the plugin's beta manifest URL
    /// is registered in Jellyfin's plugin repository list. When true, beta
    /// releases appear in Dashboard → Plugins → Catalog like any other plugin.
    /// </summary>
    public bool UseBetaChannel { get; set; }
```

- [ ] **Step 2: Add the failing test**

Append to `Jellyfin.Xtream.Library.Tests/PluginConfigurationTests.cs`:

```csharp
    [Fact]
    public void UseBetaChannel_DefaultsToFalse()
    {
        var config = new PluginConfiguration();
        config.UseBetaChannel.Should().BeFalse();
    }
```

- [ ] **Step 3: Run test to verify it passes**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PluginConfigurationTests.UseBetaChannel_DefaultsToFalse"`
Expected: 1 passed, 0 failed.

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Xtream.Library/PluginConfiguration.cs \
        Jellyfin.Xtream.Library.Tests/PluginConfigurationTests.cs
git commit -m "feat(config): add UseBetaChannel flag

Defaults to false. Storage only; the BetaChannelManager service in the
next commit will act on this flag."
```

---

## Task 2: Implement pure decision logic with unit tests

**Goal:** Land `ComputeNextRepositories` and `StructurallyEqual` as pure static methods on a new `BetaChannelManager` shell, with the full unit-test matrix passing. No DI, no hosted service wiring yet — that's Task 3.

**Files:**
- Create: `Jellyfin.Xtream.Library/Service/BetaChannelManager.cs`
- Create: `Jellyfin.Xtream.Library.Tests/Service/BetaChannelManagerTests.cs`

**Acceptance Criteria:**
- [ ] `BetaChannelManager.ComputeNextRepositories(useBeta, current)` matches the algorithm in the spec (URL match case-insensitive, append on enable when absent, remove on disable, no-op otherwise, never modify enabled/name on existing match).
- [ ] `BetaChannelManager.StructurallyEqual(a, b)` returns `true` only when arrays have identical `Name`/`Url`/`Enabled` triples in order.
- [ ] All 11 unit tests in the table below pass.
- [ ] No additional production code wiring is added yet.

**Verify:** `dotnet test -c Release --filter "FullyQualifiedName~BetaChannelManagerTests"` → 11 passed, 0 failed.

**Steps:**

- [ ] **Step 1: Write the test file first (TDD red)**

Create `Jellyfin.Xtream.Library.Tests/Service/BetaChannelManagerTests.cs`:

```csharp
// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Model.Updates;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class BetaChannelManagerTests
{
    private static RepositoryInfo Beta(string? url = null, bool enabled = true, string? name = null) =>
        new()
        {
            Name = name ?? BetaChannelManager.BetaRepoName,
            Url = url ?? BetaChannelManager.BetaRepoUrl,
            Enabled = enabled,
        };

    private static RepositoryInfo OtherRepo() =>
        new() { Name = "Jellyfin Official", Url = "https://repo.jellyfin.org/files/plugin/manifest.json", Enabled = true };

    [Fact]
    public void Enable_NoEntries_AppendsBetaRepo()
    {
        var next = BetaChannelManager.ComputeNextRepositories(true, System.Array.Empty<RepositoryInfo>());

        next.Should().HaveCount(1);
        next[0].Name.Should().Be(BetaChannelManager.BetaRepoName);
        next[0].Url.Should().Be(BetaChannelManager.BetaRepoUrl);
        next[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enable_OtherEntriesOnly_AppendsBetaRepo()
    {
        var other = OtherRepo();

        var next = BetaChannelManager.ComputeNextRepositories(true, new[] { other });

        next.Should().HaveCount(2);
        next[0].Should().BeSameAs(other);
        next[1].Url.Should().Be(BetaChannelManager.BetaRepoUrl);
    }

    [Fact]
    public void Enable_BetaAlreadyPresent_NoOp()
    {
        var current = new[] { OtherRepo(), Beta() };

        var next = BetaChannelManager.ComputeNextRepositories(true, current);

        next.Should().BeEquivalentTo(current, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void Enable_BetaPresentButRenamedByUser_NoOp()
    {
        var current = new[] { Beta(name: "My Custom Beta Name") };

        var next = BetaChannelManager.ComputeNextRepositories(true, current);

        next.Should().HaveCount(1);
        next[0].Name.Should().Be("My Custom Beta Name");
    }

    [Fact]
    public void Enable_BetaPresentButDisabledByUser_LeavesDisabled()
    {
        var current = new[] { Beta(enabled: false) };

        var next = BetaChannelManager.ComputeNextRepositories(true, current);

        next.Should().HaveCount(1);
        next[0].Enabled.Should().BeFalse();
    }

    [Fact]
    public void Disable_BetaPresent_Removes()
    {
        var other = OtherRepo();
        var current = new[] { other, Beta() };

        var next = BetaChannelManager.ComputeNextRepositories(false, current);

        next.Should().HaveCount(1);
        next[0].Should().BeSameAs(other);
    }

    [Fact]
    public void Disable_BetaAbsent_NoOp()
    {
        var current = new[] { OtherRepo() };

        var next = BetaChannelManager.ComputeNextRepositories(false, current);

        next.Should().BeEquivalentTo(current, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void Disable_EmptyList_NoOp()
    {
        var next = BetaChannelManager.ComputeNextRepositories(false, System.Array.Empty<RepositoryInfo>());

        next.Should().BeEmpty();
    }

    [Fact]
    public void Match_IsCaseInsensitiveOnUrl()
    {
        var upperUrl = BetaChannelManager.BetaRepoUrl.ToUpperInvariant();
        var current = new[] { Beta(url: upperUrl) };

        var next = BetaChannelManager.ComputeNextRepositories(false, current);

        next.Should().BeEmpty();
    }

    [Fact]
    public void StructuralEqual_TrueWhenSameContents()
    {
        var a = new[] { OtherRepo(), Beta() };
        var b = new[] { OtherRepo(), Beta() };

        BetaChannelManager.StructurallyEqual(a, b).Should().BeTrue();
    }

    [Fact]
    public void StructuralEqual_FalseWhenDifferent()
    {
        var a = new[] { Beta() };
        var b = new[] { Beta(enabled: false) };

        BetaChannelManager.StructurallyEqual(a, b).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Verify the tests fail (compile error or red)**

Run: `dotnet test -c Release --filter "FullyQualifiedName~BetaChannelManagerTests" 2>&1 | tail -20`
Expected: compile error — `BetaChannelManager` not found.

- [ ] **Step 3: Create the production class with just the pure helpers**

Create `Jellyfin.Xtream.Library/Service/BetaChannelManager.cs`:

```csharp
// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Updates;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Syncs the plugin's <see cref="PluginConfiguration.UseBetaChannel"/> setting
/// into Jellyfin's <c>ServerConfiguration.PluginRepositories</c> list, so that
/// users opting in see beta releases of this plugin in Dashboard → Plugins →
/// Catalog like any other plugin.
/// </summary>
public sealed partial class BetaChannelManager
{
    /// <summary>
    /// Display name used when this manager appends the beta repository entry.
    /// Match logic uses URL only, so users renaming the entry will not break
    /// the match.
    /// </summary>
    internal const string BetaRepoName = "Xtream Library (Beta)";

    /// <summary>
    /// Manifest URL of the plugin's beta release channel.
    /// </summary>
    internal const string BetaRepoUrl =
        "https://firestaerter3.github.io/jellyfin-plugin-repo/manifest-dev.json";

    /// <summary>
    /// Computes the desired state of the plugin-repositories array given the
    /// current state and the desired <paramref name="useBeta"/> setting.
    /// Pure function — no side effects. Returns a new array; does not mutate
    /// <paramref name="current"/>.
    /// </summary>
    internal static RepositoryInfo[] ComputeNextRepositories(bool useBeta, RepositoryInfo[] current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var matchIndex = -1;
        for (var i = 0; i < current.Length; i++)
        {
            if (string.Equals(current[i].Url, BetaRepoUrl, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        if (useBeta)
        {
            if (matchIndex >= 0)
            {
                // Already registered. Respect any edits the user made (rename, disable).
                return current;
            }

            var appended = new List<RepositoryInfo>(current.Length + 1);
            appended.AddRange(current);
            appended.Add(new RepositoryInfo
            {
                Name = BetaRepoName,
                Url = BetaRepoUrl,
                Enabled = true,
            });
            return appended.ToArray();
        }

        if (matchIndex < 0)
        {
            return current;
        }

        // Remove the matched entry, preserve order of the rest.
        var filtered = new List<RepositoryInfo>(current.Length - 1);
        for (var i = 0; i < current.Length; i++)
        {
            if (i != matchIndex)
            {
                filtered.Add(current[i]);
            }
        }

        return filtered.ToArray();
    }

    /// <summary>
    /// Structural equality on <c>Name</c> + <c>Url</c> + <c>Enabled</c>, in order.
    /// Drives the "skip SaveConfiguration when nothing changed" optimisation.
    /// </summary>
    internal static bool StructurallyEqual(RepositoryInfo[] a, RepositoryInfo[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)
                || !string.Equals(a[i].Url, b[i].Url, StringComparison.Ordinal)
                || a[i].Enabled != b[i].Enabled)
            {
                return false;
            }
        }

        return true;
    }
}
```

Note the `partial` keyword — Task 3 adds the `IHostedService` half of this class in a second partial declaration. Splitting the file keeps the pure-logic half independently readable.

- [ ] **Step 4: Run tests to verify they pass (green)**

Run: `dotnet test -c Release --filter "FullyQualifiedName~BetaChannelManagerTests"`
Expected: 11 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Xtream.Library/Service/BetaChannelManager.cs \
        Jellyfin.Xtream.Library.Tests/Service/BetaChannelManagerTests.cs
git commit -m "feat(service): add BetaChannelManager pure decision logic

ComputeNextRepositories + StructurallyEqual cover the add/remove/no-op
matrix for the beta plugin-repository entry, matching by URL only so
user renames do not break detection. Hosted-service wiring lands in the
next commit."
```

---

## Task 3: Wire `BetaChannelManager` as `IHostedService`

**Goal:** Connect the pure logic to the live `IServerConfigurationManager`. The manager subscribes to `Plugin.Instance.ConfigurationChanged` in `StartAsync`, performs an initial sync, and unsubscribes in `StopAsync`. Integration tests exercise this against a fake `IServerConfigurationManager`.

**Files:**
- Create: `Jellyfin.Xtream.Library/Service/BetaChannelManager.HostedService.cs` (second partial; pairs with `BetaChannelManager.cs` from Task 2)
- Modify: `Jellyfin.Xtream.Library/PluginServiceRegistrator.cs` (add hosted service registration)
- Create: `Jellyfin.Xtream.Library.Tests/Service/BetaChannelManagerIntegrationTests.cs`

**Acceptance Criteria:**
- [ ] `BetaChannelManager` implements `IHostedService`.
- [ ] `StartAsync` reads `Plugin.Instance.Configuration.UseBetaChannel`, runs sync once, and subscribes to `ConfigurationChanged`.
- [ ] `StopAsync` unsubscribes from `ConfigurationChanged`.
- [ ] Sync calls `IServerConfigurationManager.SaveConfiguration()` only when `StructurallyEqual` returns false.
- [ ] `IOException` and `UnauthorizedAccessException` thrown by `SaveConfiguration` are caught and logged at warning level — they do not propagate.
- [ ] `Plugin.Instance` null at `StartAsync` time is logged at info level and treated as "skip initial sync".
- [ ] All four integration tests pass.
- [ ] `BetaChannelManager` is registered as a hosted service in `PluginServiceRegistrator`.
- [ ] `dotnet build -c Release` succeeds with no new warnings.

**Verify:**
- `dotnet test -c Release --filter "FullyQualifiedName~BetaChannelManager"` → 15 passed (11 unit + 4 integration), 0 failed.
- `dotnet build -c Release` → 0 errors, 0 new warnings.

**Steps:**

- [ ] **Step 1: Write the integration tests first (red)**

Create `Jellyfin.Xtream.Library.Tests/Service/BetaChannelManagerIntegrationTests.cs`:

```csharp
// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class BetaChannelManagerIntegrationTests
{
    /// <summary>
    /// In-memory fake of IServerConfigurationManager. Tracks how many times
    /// SaveConfiguration is called so we can assert the optimisation works.
    /// Only the surface BetaChannelManager actually touches is implemented;
    /// everything else throws NotSupportedException to keep accidental
    /// dependencies obvious.
    /// </summary>
    private sealed class FakeServerConfigurationManager : IServerConfigurationManager
    {
        public ServerConfiguration Configuration { get; } = new() { PluginRepositories = Array.Empty<RepositoryInfo>() };
        public int SaveCount { get; private set; }
        public IServerApplicationPaths ApplicationPaths => throw new NotSupportedException();
        public event EventHandler<EventArgs>? ConfigurationUpdated;
        public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdated;
        public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdating;
        public void AddParts(System.Collections.Generic.IEnumerable<IConfigurationFactory> factories) => throw new NotSupportedException();
        public BaseApplicationConfiguration CommonConfiguration => throw new NotSupportedException();
        public ConfigurationStore[] GetConfigurationStores() => throw new NotSupportedException();
        public object GetConfiguration(string key) => throw new NotSupportedException();
        public Type GetConfigurationType(string key) => throw new NotSupportedException();
        public void RegisterConfiguration<TFactory>() where TFactory : IConfigurationFactory => throw new NotSupportedException();
        public void ReplaceConfiguration(BaseApplicationConfiguration newConfiguration) => throw new NotSupportedException();
        public void SaveConfiguration() { SaveCount++; }
        public void SaveConfiguration(string key, object configuration) => throw new NotSupportedException();

        // No-op suppressions so the unused-event-warning doesn't fire under strict ruleset.
        public void RaiseConfigurationUpdated() => ConfigurationUpdated?.Invoke(this, EventArgs.Empty);
        public void RaiseNamedConfigurationUpdating(ConfigurationUpdateEventArgs e) => NamedConfigurationUpdating?.Invoke(this, e);
        public void RaiseNamedConfigurationUpdated(ConfigurationUpdateEventArgs e) => NamedConfigurationUpdated?.Invoke(this, e);
    }

    private static BetaChannelManager Build(FakeServerConfigurationManager fake) =>
        new(fake, NullLogger<BetaChannelManager>.Instance);

    private static PluginConfiguration ConfigWith(bool useBeta) =>
        new() { UseBetaChannel = useBeta };

    [Fact]
    public async Task StartAsync_UseBetaTrue_AppendsRepoAndSaves()
    {
        var fake = new FakeServerConfigurationManager();
        // Plugin singleton may not be present in test context — manager must
        // tolerate that and fall back gracefully. We seed config explicitly:
        var manager = Build(fake);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: true));

        await manager.StartAsync(CancellationToken.None);

        fake.Configuration.PluginRepositories.Should().ContainSingle(r => r.Url == BetaChannelManager.BetaRepoUrl);
        fake.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_UseBetaFalse_NoRepoChangeNoSave()
    {
        var fake = new FakeServerConfigurationManager();
        var manager = Build(fake);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: false));

        await manager.StartAsync(CancellationToken.None);

        fake.Configuration.PluginRepositories.Should().BeEmpty();
        fake.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task ConfigurationChanged_FlipsUseBeta_SyncsAndSaves()
    {
        var fake = new FakeServerConfigurationManager();
        var manager = Build(fake);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: false));
        await manager.StartAsync(CancellationToken.None);

        manager.SetConfigurationForTesting(ConfigWith(useBeta: true));
        manager.OnConfigurationChangedForTesting();

        fake.Configuration.PluginRepositories.Should().ContainSingle(r => r.Url == BetaChannelManager.BetaRepoUrl);
        fake.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task StopAsync_Unsubscribes_NoMoreSyncs()
    {
        var fake = new FakeServerConfigurationManager();
        var manager = Build(fake);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: true));
        await manager.StartAsync(CancellationToken.None);
        var savesBeforeStop = fake.SaveCount;

        await manager.StopAsync(CancellationToken.None);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: false));
        manager.OnConfigurationChangedForTesting();

        fake.SaveCount.Should().Be(savesBeforeStop);
    }
}
```

Note: the tests use two test-only seams — `SetConfigurationForTesting` and `OnConfigurationChangedForTesting` — to avoid needing a live `Plugin.Instance` in the test context. Both are `internal` (visible via `InternalsVisibleTo`). The seams stay tiny and exist purely to make the wiring testable without booting Jellyfin.

- [ ] **Step 2: Verify the integration tests fail (compile error)**

Run: `dotnet test -c Release --filter "FullyQualifiedName~BetaChannelManagerIntegrationTests" 2>&1 | tail -10`
Expected: compile error — `BetaChannelManager` is not an `IHostedService`, `SetConfigurationForTesting` not found, etc.

- [ ] **Step 3: Add `InternalsVisibleTo` for the test assembly**

If not already present, append to `Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj` inside an `<ItemGroup>`:

```xml
    <InternalsVisibleTo Include="Jellyfin.Xtream.Library.Tests" />
```

Check first — the project may already have this. If it does, skip.

- [ ] **Step 4: Implement the hosted-service half**

Create `Jellyfin.Xtream.Library/Service/BetaChannelManager.HostedService.cs`:

```csharp
// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

public sealed partial class BetaChannelManager : IHostedService
{
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<BetaChannelManager> _logger;
    private PluginConfiguration? _configurationOverride; // test seam — see SetConfigurationForTesting
    private EventHandler<BasePluginConfiguration>? _subscribedHandler;

    public BetaChannelManager(
        IServerConfigurationManager serverConfig,
        ILogger<BetaChannelManager> logger)
    {
        _serverConfig = serverConfig ?? throw new ArgumentNullException(nameof(serverConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Sync();

        _subscribedHandler = (_, _) => OnConfigurationChanged();
        var plugin = TryGetPluginInstance();
        if (plugin is not null)
        {
            plugin.ConfigurationChanged += _subscribedHandler;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribedHandler is not null)
        {
            var plugin = TryGetPluginInstance();
            if (plugin is not null)
            {
                plugin.ConfigurationChanged -= _subscribedHandler;
            }

            _subscribedHandler = null;
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged()
    {
        try
        {
            Sync();
        }
        catch (Exception ex)
        {
            // A sync failure must never abort the user's config save.
            _logger.LogWarning(ex, "BetaChannelManager sync failed during ConfigurationChanged handler.");
        }
    }

    private void Sync()
    {
        var config = ResolveConfiguration();
        if (config is null)
        {
            _logger.LogInformation("BetaChannelManager: plugin instance not yet available; skipping sync. Will reconcile on next config save.");
            return;
        }

        var current = _serverConfig.Configuration.PluginRepositories ?? Array.Empty<MediaBrowser.Model.Updates.RepositoryInfo>();
        var next = ComputeNextRepositories(config.UseBetaChannel, current);

        if (StructurallyEqual(next, current))
        {
            return;
        }

        _serverConfig.Configuration.PluginRepositories = next;
        try
        {
            _serverConfig.SaveConfiguration();
            _logger.LogInformation(
                "BetaChannelManager: {Action} beta repository entry (UseBetaChannel={UseBetaChannel}).",
                config.UseBetaChannel ? "added" : "removed",
                config.UseBetaChannel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "BetaChannelManager: could not persist updated plugin repository list. Will retry on next config save.");
        }
    }

    private PluginConfiguration? ResolveConfiguration()
    {
        if (_configurationOverride is not null)
        {
            return _configurationOverride;
        }

        return TryGetPluginInstance()?.Configuration;
    }

    private static Plugin? TryGetPluginInstance()
    {
        try
        {
            return Plugin.Instance;
        }
        catch (InvalidOperationException)
        {
            // Plugin singleton not assigned yet (startup race). Caller treats null as "skip".
            return null;
        }
    }

    // ---- Test seams ----

    internal void SetConfigurationForTesting(PluginConfiguration config) => _configurationOverride = config;

    internal void OnConfigurationChangedForTesting() => OnConfigurationChanged();
}
```

- [ ] **Step 5: Register the hosted service**

Modify `Jellyfin.Xtream.Library/PluginServiceRegistrator.cs`. Inside `RegisterServices`, after the existing `AddSingleton<IScheduledTask, SyncLibraryTask>()` line, add:

```csharp
        serviceCollection.AddHostedService<Service.BetaChannelManager>();
```

- [ ] **Step 6: Run the full BetaChannelManager test suite**

Run: `dotnet test -c Release --filter "FullyQualifiedName~BetaChannelManager"`
Expected: 15 passed (11 unit + 4 integration), 0 failed.

- [ ] **Step 7: Build the whole solution to confirm no warning regressions**

Run: `dotnet build -c Release 2>&1 | tail -20`
Expected: `Build succeeded` with 0 errors and 0 new warnings (the project uses TreatWarningsAsErrors).

- [ ] **Step 8: Commit**

```bash
git add Jellyfin.Xtream.Library/Service/BetaChannelManager.HostedService.cs \
        Jellyfin.Xtream.Library/PluginServiceRegistrator.cs \
        Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj \
        Jellyfin.Xtream.Library.Tests/Service/BetaChannelManagerIntegrationTests.cs
git commit -m "feat(service): wire BetaChannelManager as IHostedService

StartAsync reconciles the plugin's beta repo entry on boot and
subscribes to Plugin.ConfigurationChanged; StopAsync unsubscribes.
SaveConfiguration failures are logged at warning level rather than
propagated, so a transient disk error never aborts the user's plugin
config save."
```

---

## Task 4: Add "Updates" section to the config UI

**Goal:** Surface the new flag in the General tab as a single labeled checkbox with description text, and wire it into `config.js`'s load + save paths.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Configuration/Web/config.html` (insert a new `<div class="verticalSection">` between "Library Settings" and "Advanced Settings", around line 451)
- Modify: `Jellyfin.Xtream.Library/Configuration/Web/config.js` (add to the global load block around line 264 and the global save block around line 337)

**Acceptance Criteria:**
- [ ] A new "Updates" section appears in the General tab with a "Use beta channel" checkbox.
- [ ] The checkbox state loads from `config.UseBetaChannel`.
- [ ] Clicking Save persists `config.UseBetaChannel`.
- [ ] No new JS console errors when opening the page.

**Verify:** Build the plugin, sideload onto the dev server (host bind path on the Jellyfin machine; see `CLAUDE.local.md` for chown invariant), open Dashboard → Plugins → Xtream Library, confirm the checkbox is present in the General tab, toggle it, click Save, refresh the page, confirm the toggled state survived.

**Steps:**

- [ ] **Step 1: Add the Updates section to config.html**

Locate the General tab's `<!-- General Tab -->` block and the existing `<div class="verticalSection">` containing the "Advanced Settings" advancedToggle (around line 451 in `config.html`).

Immediately before that `verticalSection`, insert:

```html
                        <div class="verticalSection">
                            <h3 class="sectionTitle">Updates</h3>
                            <div class="checkboxContainer checkboxContainer-withDescription">
                                <label class="emby-checkbox-label">
                                    <input is="emby-checkbox" type="checkbox" id="chkUseBetaChannel" name="UseBetaChannel" />
                                    <span>Use beta channel</span>
                                </label>
                                <div class="fieldDescription checkboxFieldDescription">
                                    Receive pre-release versions of Xtream Library. Enabling this registers the beta plugin repository with Jellyfin; updates appear under Dashboard &rarr; Plugins &rarr; Catalog like any other plugin. Disabling removes the entry. Beta releases may contain bugs or incomplete features &mdash; opting in is how you help catch regressions before they reach the stable channel.
                                </div>
                            </div>
                        </div>
```

- [ ] **Step 2: Wire the checkbox into config.js load path**

Open `Jellyfin.Xtream.Library/Configuration/Web/config.js`. Find the "Global settings (not per-provider)" block (around line 261). Immediately after the line:

```javascript
            document.getElementById('chkEnableMetadataLookup').checked = config.EnableMetadataLookup !== false;
```

add:

```javascript
            document.getElementById('chkUseBetaChannel').checked = config.UseBetaChannel === true;
```

- [ ] **Step 3: Wire the checkbox into config.js save path**

In the same file, find the "Global settings only" block in the save handler (around line 336). Immediately after the line:

```javascript
            config.EnableMetadataLookup = document.getElementById('chkEnableMetadataLookup').checked;
```

add:

```javascript
            config.UseBetaChannel = document.getElementById('chkUseBetaChannel').checked;
```

- [ ] **Step 4: Confirm build still succeeds**

Run: `dotnet build -c Release 2>&1 | tail -5`
Expected: `Build succeeded` with 0 errors.

Embedded HTML/JS are part of the assembly's resources; the build verifies they're well-formed enough to embed.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Xtream.Library/Configuration/Web/config.html \
        Jellyfin.Xtream.Library/Configuration/Web/config.js
git commit -m "feat(ui): add 'Use beta channel' checkbox to General tab

Surfaces UseBetaChannel in a new Updates section. BetaChannelManager
(landed earlier) handles the actual repository-list sync; this commit
just adds the form binding."
```

---

## Task 5: Manual verification on the dev Jellyfin server

**Goal:** Confirm end-to-end behaviour on the real server before bumping the version: the checkbox round-trips, Jellyfin's `system.xml` gains/loses the beta `RepositoryInfo` entry, and the plugin catalog reflects the change.

**Files:**
- None (verification only).

**Acceptance Criteria:**
- [ ] Sideloaded plugin shows the "Use beta channel" checkbox in the General tab, unchecked by default.
- [ ] Toggling ON + Save adds a `RepositoryInfo` entry with the beta manifest URL to `system.xml`.
- [ ] Restart Jellyfin → entry survives.
- [ ] Dashboard → Plugins → Catalog → Available shows beta-tagged Xtream Library versions while the toggle is ON.
- [ ] Toggling OFF + Save removes the entry from `system.xml`.
- [ ] Restart Jellyfin → entry stays removed.

**Verify:** Inspect `system.xml` on the dev server (host bind path documented in `CLAUDE.local.md`) and check Dashboard → Plugins → Catalog visually for beta entries.

**Steps:**

- [ ] **Step 1: Build the release DLL**

Run:
```bash
dotnet publish Jellyfin.Xtream.Library -c Release -o /tmp/claude/xtream-library-release
```

- [ ] **Step 2: Note current plugin version**

The version determines the destination plugin folder name. Read `<AssemblyVersion>` from `Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj` — call this `<VER>`.

- [ ] **Step 3: Sideload onto the dev server**

Using the IP, password, SSH/SCP fallback paths, and the userns-remap chown invariant from this project's `CLAUDE.local.md` (gitignored — never quote credential values into the plan), copy `Jellyfin.Xtream.Library.dll` into the existing versioned plugin folder on the host (path under the bind-mounted plugin directory, named `Xtream Library_<VER>`), then `chown -R 166536:166536` that folder.

Restart the container: `docker exec jellyfin sh -c "kill 1"` (or equivalent — pick whatever you've used before; the project notes hold the working incantation).

- [ ] **Step 4: Verify the checkbox is present and unchecked**

Visit Dashboard → Plugins → Xtream Library → General tab. Confirm the "Updates" section appears with an unchecked "Use beta channel" checkbox.

- [ ] **Step 5: Toggle ON and persist**

Check the box. Click Save. Watch the network panel: the PUT to the plugin config endpoint should return 200/204.

- [ ] **Step 6: Confirm `system.xml` reflects the change**

On the Jellyfin host, dump the relevant section:

```bash
# (executed via your normal SSH path; do not paste credentials into the tool args)
docker exec jellyfin sh -c "cat /config/config/system.xml" | grep -A2 -B1 manifest-dev.json
```

Expected: a `<RepositoryInfo>` block with `<Url>https://firestaerter3.github.io/jellyfin-plugin-repo/manifest-dev.json</Url>` and `<Enabled>true</Enabled>`.

- [ ] **Step 7: Confirm catalog updates**

Dashboard → Plugins → Catalog → Available. Beta-tagged Xtream Library versions (e.g. v1.36.x while stable is at v1.34.0.0) should appear in the list.

- [ ] **Step 8: Restart and confirm the toggle survives**

`docker exec jellyfin sh -c "kill 1"` (or your usual restart command). After Jellyfin comes back, reopen the plugin config: checkbox still ticked. `grep manifest-dev system.xml` still finds the entry.

- [ ] **Step 9: Toggle OFF and re-verify**

Uncheck the box. Click Save. Confirm `system.xml` no longer contains the beta entry. Catalog shows only stable versions. Restart again, confirm the off state persists.

- [ ] **Step 10: Note results in BUGS.md / release notes**

If any step misbehaved, file the symptom in `BUGS.md` and stop the release. Otherwise: this task closes and the next manual step (out of plan scope) is the normal release process per `CLAUDE.md`: bump version, tag, build, GitHub release, add to `manifest-dev.json` in the sibling repo.

---

## Self-review

Run after Task 5 closes.

1. **Spec coverage** — every section of the spec maps to a task:
   - PluginConfiguration field → Task 1 ✓
   - `BetaChannelManager` + `ComputeNextRepositories` + `StructurallyEqual` → Task 2 ✓
   - `IHostedService` wiring + startup/event/shutdown flow → Task 3 ✓
   - Error handling for `SaveConfiguration` exceptions + null `Plugin.Instance` → Task 3 ✓
   - DI registration → Task 3 ✓
   - Config UI → Task 4 ✓
   - Manual verification → Task 5 ✓
   - Unit + integration test matrix → Tasks 2 and 3 ✓

2. **Placeholder scan** — no TBD, no TODO, no "add appropriate X", no "similar to Task N". Each step shows the actual code.

3. **Type consistency** — `BetaRepoName` and `BetaRepoUrl` referenced identically in production code and tests. `ComputeNextRepositories` and `StructurallyEqual` signatures match across Task 2 (definition) and Task 3 (caller). `UseBetaChannel` named consistently across PluginConfiguration, tests, config.js, and config.html. The `partial` keyword on the class is declared in Task 2 and required by Task 3's second partial.

No issues found.
