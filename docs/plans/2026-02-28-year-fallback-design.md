# Design: Year-Free Metadata Lookup Fallback

**Date:** 2026-02-28
**Issue:** [#21 – Fallback to lookup without year](https://github.com/firestaerter3/Jellyfin-Xtream-Library/issues/21)

## Problem

Some Xtream providers embed incorrect years in stream names (e.g. `The Notebook (2009)` instead of 2004). When the plugin extracts this year and passes it to TMDb/TVDb, the search either returns no results or the wrong movie. The lookup fails silently, no TMDb ID is written to the NFO, and Jellyfin cannot match the item to its metadata.

## Scope

Both movies (TMDb) and series (TVDb).

## Design Decision: Opt-In Config Toggle

Year-free fallback is **disabled by default** (`FallbackToYearlessLookup = false`). Without the year constraint, `IsLikelyFalsePositive` loses its year-based checks (Check 1 and Check 2), leaving only the short-title check (Check 3). Users with providers that have consistently wrong years can enable this feature knowing the tradeoff.

## Components

### 1. `PluginConfiguration.cs`

Add one new property in the metadata section:

```csharp
/// <summary>
/// Gets or sets a value indicating whether to retry metadata lookup without the year
/// if the year-qualified lookup returns no result.
/// Useful when provider stream names contain incorrect years.
/// Disabled by default as it weakens false-positive protection.
/// </summary>
public bool FallbackToYearlessLookup { get; set; } = false;
```

No clamping needed.

### 2. `MetadataLookupService.cs`

#### `LookupMovieTmdbIdAsync`

After the primary lookup returns `null`, and if all three conditions hold:
1. `year.HasValue` (a year was present in the stream name)
2. `config.FallbackToYearlessLookup == true`
3. The year-free cache key has no valid cached result

...execute a second provider search with `year = null`, cache the result under `movie:{title}` (year-free key), and return.

If the fallback also returns null, cache null under the year-free key to prevent repeated API calls.

#### `LookupSeriesTvdbIdAsync`

Same pattern, using `series:{title}` as the year-free cache key.

#### Flow diagram

```
year-qualified lookup
  → cache hit (non-null)  → return cached ID
  → cache hit (null)      → skip (already known miss)
  → cache miss            → live lookup
      → found             → cache + return
      → not found         → FallbackToYearlessLookup enabled AND year.HasValue?
            → no          → cache null + return null
            → yes         → year-free live lookup
                              → found  → cache year-free + return
                              → not found → cache year-free null + return null
```

#### Logging

- `LogInformation` when fallback fires: `"Retrying {Type} metadata lookup without year for: '{Title}' (extracted year={Year})"`
- Normal debug log for the fallback result.

### 3. `config.html`

Add a checkbox directly below the `chkEnableMetadataLookup` checkbox, same visual style:

```html
<div class="checkboxContainer checkboxContainer-withDescription">
    <label class="emby-checkbox-label">
        <input is="emby-checkbox" type="checkbox" id="chkFallbackToYearlessLookup" name="FallbackToYearlessLookup" />
        <span>Fallback to Year-Free Lookup</span>
    </label>
    <div class="fieldDescription checkboxFieldDescription">
        If a year-qualified metadata lookup returns no result, retry without the year.
        Enable this if your provider has many incorrect years in stream names. Note: weakens
        false-positive protection for ambiguous titles.
    </div>
</div>
```

### 4. `config.js`

Bind and save `FallbackToYearlessLookup` using the same pattern as other checkboxes
(`chkFallbackToYearlessLookup` ↔ `config.FallbackToYearlessLookup`).

### 5. Tests (`MetadataLookupServiceTests.cs`)

New test cases:
- **Fallback fires:** `FallbackToYearlessLookup=true`, year-qualified lookup returns null, year-free lookup returns a TMDb ID → ID returned.
- **Fallback skipped (disabled):** `FallbackToYearlessLookup=false` → only one provider call, null returned.
- **No double lookup when year=null:** Stream had no year; the initial lookup is already year-free → no second call even when fallback is enabled.
- **Fallback null cached:** Year-free lookup also returns no result → null cached under year-free key.

## Files Changed

| File | Change |
|------|--------|
| `PluginConfiguration.cs` | Add `FallbackToYearlessLookup` bool property |
| `Service/MetadataLookupService.cs` | Add fallback retry logic in both lookup methods |
| `Configuration/Web/config.html` | Add checkbox for new config field |
| `Configuration/Web/config.js` | Bind/save new checkbox |
| `Tests/Service/MetadataLookupServiceTests.cs` | Add 4 new test cases |

## Out of Scope

- Title similarity scoring for fallback results (would break cross-language matching)
- Configurable year mismatch tolerance (separate concern)
