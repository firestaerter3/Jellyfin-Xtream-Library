# Beta Channel Toggle — Design

**Status:** Draft
**Date:** 2026-06-04
**Goal:** Lower the friction for users to opt in to the plugin's beta release channel, so the channel actually gets traffic and can function as a real canary before changes promote to stable.

## Context

The project ships releases on two channels:

- **Stable** — `https://firestaerter3.github.io/jellyfin-plugin-repo/manifest.json` — currently at v1.34.0.0.
- **Beta** — `https://firestaerter3.github.io/jellyfin-plugin-repo/manifest-dev.json` — currently at v1.36.6.0.

Beta release downloads are 1–16 each across 7 versions; stable release downloads are 230+ on a single version. The beta channel is functioning as the maintainer's personal staging environment, not a canary. Regressions only surface at promotion time, by which point the whole user population is exposed at once.

GitHub issue #52 ("Broken manifest.json") came from a user who assumed the gap between the GitHub release page and the stable manifest was a pipeline bug. It is the documented "beta first, promote on demand" workflow, but no one outside the maintainer is reachable on the beta channel today.

A precedent for a self-managed beta toggle exists in the sister Emby plugin (`Emby.Xtream.Plugin`, kept in a separate working copy alongside this one). That implementation polls GitHub releases directly, shows an in-plugin update banner, and self-installs the DLL — bypassing Emby's plugin catalog. This design takes a smaller step suited to Jellyfin specifically.

## Decision

Add a single `UseBetaChannel` boolean to the plugin configuration. When set, the plugin programmatically registers the beta manifest URL in Jellyfin's own `ServerConfiguration.PluginRepositories` list. When cleared, it removes the entry. Updates flow through Jellyfin's standard catalog UI (Dashboard → Plugins → Catalog).

Three approaches were considered (see [Alternatives](#alternatives) below); manifest-injection was chosen for smallest surface area, reuse of Jellyfin's atomic install + ABI gating, and discoverability inside the plugin's own config page.

## Architecture

```
┌─────────────────────┐    ConfigurationChanged    ┌──────────────────────┐
│ PluginConfiguration │ ─────────────────────────► │ BetaChannelManager   │
│ - UseBetaChannel    │                            │ (IHostedService)     │
└─────────────────────┘                            │                      │
        ▲                                          │  Sync(useBeta) ──────┼──► IServerConfigurationManager
        │ user toggles in General tab              │                      │       └─ Configuration.PluginRepositories
        │                                          └──────────────────────┘            (add/remove entry by URL)
   config.html / config.js
```

Three units, each with a single responsibility:

1. **`PluginConfiguration.UseBetaChannel`** — storage for a single `bool`, default `false`.
2. **`BetaChannelManager`** — encapsulates the sync from plugin-config state to server-config state. The decision logic is in a pure static method `ComputeNextRepositories` that takes the current array and returns the desired array; everything else is plumbing.
3. **Config UI** — one checkbox in the General tab, in a new "Updates" sub-section, with description text pointing the user at Dashboard → Plugins → Catalog for the actual upgrade step.

Why a separate service and not a method on `Plugin.cs`: `BasePlugin<T>` has a fixed constructor signature `(IApplicationPaths, IXmlSerializer)`. `IServerConfigurationManager` cannot be injected there. A DI-friendly `IHostedService` is the standard Jellyfin pattern and keeps the manager unit-testable in isolation.

## Components and contracts

### `PluginConfiguration.cs`

One added property:

```csharp
/// <summary>
/// Gets or sets a value indicating whether to register the plugin's beta
/// manifest URL in Jellyfin's plugin repository list. When true, beta
/// versions of this plugin appear in Dashboard → Plugins → Catalog.
/// </summary>
public bool UseBetaChannel { get; set; }
```

Default `false`. No migration needed — existing configs deserialize to the default.

### `Service/BetaChannelManager.cs` (new)

```csharp
public sealed class BetaChannelManager : IHostedService
{
    internal const string BetaRepoName = "Xtream Library (Beta)";
    internal const string BetaRepoUrl =
        "https://firestaerter3.github.io/jellyfin-plugin-repo/manifest-dev.json";

    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<BetaChannelManager> _logger;

    public BetaChannelManager(
        IServerConfigurationManager serverConfig,
        ILogger<BetaChannelManager> logger);

    public Task StartAsync(CancellationToken ct);   // initial sync + subscribe
    public Task StopAsync(CancellationToken ct);    // unsubscribe

    // Pure decision function — exposed internal for unit testing.
    internal static RepositoryInfo[] ComputeNextRepositories(
        bool useBeta,
        RepositoryInfo[] current);

    // Internal helper for the "skip Save" optimization.
    internal static bool StructurallyEqual(
        RepositoryInfo[] a,
        RepositoryInfo[] b);
}
```

**Sync algorithm** (inside `ComputeNextRepositories`):

- Match by **URL only**, case-insensitive. `Name` is presentational; a user could have renamed an entry, and renaming should not break our ability to find it.
- `useBeta == true` and no entry matches → append `{ Name = BetaRepoName, Url = BetaRepoUrl, Enabled = true }`.
- `useBeta == true` and an entry matches → leave it alone. Do not reset `Enabled`, do not rename — respect the user's edits.
- `useBeta == false` and an entry matches → remove it.
- `useBeta == false` and no match → no-op.

**Save guard:** if the computed array `StructurallyEqual` to the current one, the manager skips `SaveConfiguration()`. This avoids spurious writes on every plugin config save (the vast majority of which won't change the beta toggle).

### `PluginServiceRegistrator.cs`

One added line at the bottom of `RegisterServices`:

```csharp
serviceCollection.AddHostedService<BetaChannelManager>();
```

### Config UI

**`Configuration/Web/config.html`** — new `verticalSection` in the General tab, placed between "Library Settings" and "Advanced Settings":

```html
<div class="verticalSection">
    <h3 class="sectionTitle">Updates</h3>
    <div class="checkboxContainer checkboxContainer-withDescription">
        <label class="emby-checkbox-label">
            <input is="emby-checkbox" type="checkbox" id="chkUseBetaChannel" name="UseBetaChannel" />
            <span>Use beta channel</span>
        </label>
        <div class="fieldDescription checkboxFieldDescription">
            Receive pre-release versions of Xtream Library. Enabling this registers
            the beta plugin repository with Jellyfin; updates appear under
            Dashboard → Plugins → Catalog like any other plugin. Disabling removes it.
            Beta releases may contain bugs or incomplete features — opting in is
            how you help catch regressions before they reach the stable channel.
        </div>
    </div>
</div>
```

**`Configuration/Web/config.js`** — load/save the new boolean alongside the other General-tab fields. Follows the existing pattern (no new mechanics).

## Data flow

### Startup (server boot or plugin reload)

```
Jellyfin starts
  └─► IHostedService.StartAsync on BetaChannelManager
        ├─► Read Plugin.Instance.Configuration.UseBetaChannel
        ├─► Read _serverConfig.Configuration.PluginRepositories
        ├─► next = ComputeNextRepositories(useBeta, current)
        ├─► if (!StructurallyEqual(next, current))
        │     ├─► _serverConfig.Configuration.PluginRepositories = next
        │     └─► _serverConfig.SaveConfiguration()
        └─► Plugin.Instance.ConfigurationChanged += OnConfigurationChanged
```

State on disk in Jellyfin's `system.xml` is reconciled at every boot to whatever the plugin's `UseBetaChannel` says. If a user manually deletes our entry while the toggle is still on, the next boot re-adds it.

### Toggle (user changes the checkbox and clicks Save)

```
config.js POSTs the updated PluginConfiguration via Jellyfin's standard endpoint
  └─► Jellyfin deserializes + persists Jellyfin.Xtream.Library.xml
        └─► BasePlugin raises ConfigurationChanged
              └─► BetaChannelManager.OnConfigurationChanged
                    └─► (same Sync(useBeta) flow as startup)
```

### Shutdown

```
Jellyfin stops
  └─► IHostedService.StopAsync
        └─► Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged
```

No cleanup of the repository entry on shutdown — the entry is meant to persist across restarts.

### Re-entrancy

`_serverConfig.SaveConfiguration()` does **not** trigger `Plugin.Instance.ConfigurationChanged` — those are two separate event sources (server config vs. plugin config). No event loop.

### Startup race

If `StartAsync` runs before `Plugin.Instance` is set (the singleton assignment happens inside the plugin constructor), accessing `Plugin.Instance.Configuration` would throw `InvalidOperationException`. In practice Jellyfin constructs plugin instances before starting hosted services, but the initial-sync block guards against it with a try/catch that logs and skips. The next `ConfigurationChanged` reconciles.

## Error handling

| Failure | Response |
|---|---|
| `SaveConfiguration()` throws (`IOException`, `UnauthorizedAccessException`) | Catch, log a warning, keep running. In-memory mutation is left in place — Jellyfin's next successful save flushes it. |
| `Plugin.Instance` null in `StartAsync` | Log info, skip initial sync. The next `ConfigurationChanged` reconciles. |
| Exception thrown inside `OnConfigurationChanged` handler | Wrap handler body in try/catch; log + swallow. A sync failure must never abort the user's config save. |
| Beta URL unreachable (GitHub Pages down, DNS, firewall) | Not our concern. Jellyfin's catalog fetcher surfaces that, not us. Our job ends at registering the URL. |
| User had the beta repo under a different `Name` | Match by URL only — we still find it. |
| User has the beta URL with a stale URL we no longer ship | Out of scope. If we ever change the URL, ship a one-shot migration alongside that change. |

**Deliberately omitted:**

- No URL validation — `BetaRepoUrl` is a compile-time constant.
- No retry loop — Jellyfin's config-save path is already the retry boundary.
- No locking — `ConfigurationChanged` is single-threaded per plugin and `StartAsync` runs once; there is no concurrent writer.

## Testing

### `BetaChannelManagerTests.cs` (new)

Pure unit tests around `ComputeNextRepositories` and `StructurallyEqual`. No DI, no host, no I/O.

| Test | Pre-state | Action | Assert |
|---|---|---|---|
| `Enable_NoEntries_AppendsBetaRepo` | `[]` | `useBeta=true` | one entry: name=`BetaRepoName`, url=`BetaRepoUrl`, enabled=true |
| `Enable_OtherEntriesOnly_AppendsBetaRepo` | `[other]` | `useBeta=true` | `[other, beta]`; `other` untouched |
| `Enable_BetaAlreadyPresent_NoOp` | `[beta]` | `useBeta=true` | unchanged |
| `Enable_BetaPresentButRenamedByUser_NoOp` | `[{name="Custom", url=BetaRepoUrl}]` | `useBeta=true` | unchanged — match by URL |
| `Enable_BetaPresentButDisabledByUser_LeavesDisabled` | `[{url=BetaRepoUrl, enabled=false}]` | `useBeta=true` | still `enabled=false` |
| `Disable_BetaPresent_Removes` | `[other, beta]` | `useBeta=false` | `[other]` |
| `Disable_BetaAbsent_NoOp` | `[other]` | `useBeta=false` | unchanged |
| `Disable_EmptyList_NoOp` | `[]` | `useBeta=false` | empty |
| `Match_IsCaseInsensitiveOnUrl` | `[{url=BETA_URL_UPPER}]` | `useBeta=false` | removes the upper-case entry |
| `StructuralEqual_TrueWhenSameContents` | identical arrays | direct call | true |
| `StructuralEqual_FalseWhenDifferent` | differing on any field | direct call | false |

### `BetaChannelManagerIntegrationTests.cs` (new)

Exercises the `IHostedService` wiring against an in-memory fake `IServerConfigurationManager` (a ~30-line test double — Jellyfin's real implementation is not pulled in).

| Test | Assertion |
|---|---|
| `StartAsync_UseBetaTrue_AppendsRepoAndSaves` | The fake's `SaveConfiguration` is called exactly once; resulting `PluginRepositories` contains the beta entry. |
| `StartAsync_UseBetaFalse_NoRepoChangeNoSave` | `SaveConfiguration` not called when state already matches the desired target. |
| `ConfigurationChanged_FlipsUseBeta_SyncsAndSaves` | After firing the plugin event, the fake reflects the new state. |
| `StopAsync_Unsubscribes_NoMoreSyncs` | Firing the event after `StopAsync` produces no further `SaveConfiguration` call. |

### `PluginConfigurationTests.cs` (extend)

| Test | Assertion |
|---|---|
| `UseBetaChannel_DefaultsToFalse` | New installs do not opt into beta automatically. |

### Manual verification (one-time, before release)

1. Fresh server with plugin installed via stable manifest only. Toggle ON, Save. Verify `system.xml` now contains the beta `RepositoryInfo` entry. Open Dashboard → Plugins → Catalog → Available; beta-tagged Xtream Library versions appear.
2. Toggle OFF, Save. Verify the entry is removed from `system.xml`. Catalog shows only stable versions again.
3. Restart Jellyfin between each toggle. Initial state survives the restart.

## Alternatives considered

### B. Self-managed banner + install (Emby parity)

Port `UpdateChecker` and `InstallUpdate` from the Emby plugin. Plugin polls GitHub releases directly, shows an update banner in its config page, downloads the DLL on click.

- **Pro:** Best UX — toggle and "Install update" sit in the same panel.
- **Con:** Big code drop (~7–8 files). Must adapt for Jellyfin's versioned plugin folders (`Xtream Library_<ver>/` with `meta.json`) rather than a flat DLL. Duplicates work Jellyfin's catalog already does. Bypasses `TargetAbi` gating — a 10.12-only beta could install on a 10.11 server and silently fail to load. Needs care for the userns-remap chown gotcha in `CLAUDE.local.md`.

Rejected: too much surface area for a workflow Jellyfin's catalog already handles correctly. The user's stated goal was the Emby toggle pattern, but Emby's choice was driven by Emby's catalog UX, which doesn't apply on Jellyfin.

### C. Docs-only

Checkbox just unhides a section with the URL, a Copy button, and instructions to paste into Dashboard → Plugins → Repositories.

- **Pro:** Trivial implementation.
- **Con:** Barely lowers opt-in friction. Equivalent to what we already commented on issue #52. Won't grow the beta cohort meaningfully.

Rejected: doesn't fix the canary problem.

## Out of scope

- Update notifications inside the plugin UI (no banner). If wanted later, can be added as an `IHostedService` background poll.
- Forcing beta-channel installs over stable when both are available. That stays in Jellyfin's hands via the standard catalog UI's "Show beta" behavior.
- Migration if the beta manifest URL ever changes. One-shot migration would be added with that change.
- Cleanup of the beta repo entry on plugin uninstall. Jellyfin doesn't notify plugins of uninstall; the orphan entry is harmless.

## Build sequence

1. Add `UseBetaChannel` field to `PluginConfiguration`. Extend `PluginConfigurationTests` with default-value test.
2. Implement `BetaChannelManager` (`StartAsync`, `StopAsync`, `OnConfigurationChanged`, `ComputeNextRepositories`, `StructurallyEqual`). Write `BetaChannelManagerTests`.
3. Write `BetaChannelManagerIntegrationTests` using a fake `IServerConfigurationManager`.
4. Register `BetaChannelManager` as a hosted service in `PluginServiceRegistrator`.
5. Add the "Updates" section + checkbox to `config.html`; wire load/save in `config.js`.
6. Manual verification on the dev server. Bump version, release to beta channel.
