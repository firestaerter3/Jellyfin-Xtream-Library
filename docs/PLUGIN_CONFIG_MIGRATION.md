# Plugin Configuration Migration

How `PluginConfiguration` migrates legacy single-provider settings into the multi-provider `Providers` collection on first startup of v1.32+ — and the .NET 9 XmlSerializer pitfall that broke it once.

## The migration

`Plugin.MigrateConfigurationIfNeeded` runs from the plugin constructor. It detects an unmigrated single-provider config (legacy `BaseUrl` populated, `Providers` empty), copies the relevant fields into a freshly constructed `ProviderConfig`, appends it to `Providers`, and saves.

```csharp
private void MigrateConfigurationIfNeeded()
{
    var config = Configuration;
    if (config.Providers.Count != 0 || string.IsNullOrEmpty(config.BaseUrl))
    {
        return;
    }

    config.Providers.Add(new ProviderConfig
    {
        BaseUrl = config.BaseUrl,
        Username = config.Username,
        // ... rest of the legacy fields
    });
    SaveConfiguration();
}
```

This pattern relies on the legacy properties (`BaseUrl`, `Username`, `Password`, etc) **deserializing correctly from the on-disk XML**. If those reads come back empty, the guard takes the early-return branch and `Providers` stays empty forever — Live TV, sync, and stream URL generation all break.

## Why `[Obsolete]` on the legacy fields is forbidden

It is tempting to mark the legacy properties `[Obsolete("Migrated to Providers[0]...")]` so new code that reads them gets a compile warning. **Do not do this.**

`System.Xml.Serialization.XmlSerializer` on .NET 9 silently skips properties with `[ObsoleteAttribute]` during deserialization. The property keeps its field-initializer default (empty string, `false`, etc) regardless of what is in the XML element. There is no error, no warning, no log line — the property just appears empty in the deserialized object.

Confirmed against a real v1.31 schema config in-container (see `BUG-005` in `BUGS.md`):

```
BaseUrl property: System.String BaseUrl
  attributes: XmlElementAttribute, ObsoleteAttribute
BaseUrl value: []           ← XML had <BaseUrl>http://provider:8080</BaseUrl>
Providers.Count: 0
early return                ← migration never ran
```

Adding `[XmlElement]` did not override the skip on .NET 9. (It does on .NET 10 — separate runtime quirk. Do not rely on either.)

This is also why `PluginConfiguration` removes the `[Obsolete]` attribute from the legacy block but adds:

```csharp
#pragma warning disable SA1623
// === Legacy fields — kept for XML deserialization during migration only.
// Read by MigrateConfigurationIfNeeded() in Plugin.cs.
// (Cannot use [Obsolete] here — .NET 9's XmlSerializer skips obsolete
// properties during deserialization, which would defeat migration.)
// ===
```

## Regression tests

`PluginConfigurationTests` has two tests that pin this behavior. They fail loudly the moment `[Obsolete]` reappears on any legacy property:

- `XmlDeserialize_LegacyV131Schema_PopulatesLegacyFields` — deserializes a v1.31-shaped XML and asserts the legacy fields are populated.
- `XmlRoundtrip_DefaultConfig_DeserializesWithoutError` — serializes a default `PluginConfiguration` and round-trips it to catch any new schema-incompatible defaults.

## Adding a new legacy field

If you ever need to add another legacy field to migrate (for example, retiring a global setting in favor of per-provider):

1. Add the new property to `PluginConfiguration` in the `// Legacy fields` block — **plain property, no `[Obsolete]`**.
2. Read it in `MigrateConfigurationIfNeeded` and assign into the new `ProviderConfig` (or its destination).
3. Extend `XmlDeserialize_LegacyV131Schema_PopulatesLegacyFields` to include the new element in the test XML and assert it deserializes.
4. Discourage new readers via a code review comment, not via `[Obsolete]`. A leading `// migration-only` comment on the property declaration is enough.

## Removing a legacy field

Only delete a legacy property once you are confident no production XML in the wild still contains the corresponding element. The XmlSerializer ignores unknown elements silently, so leaving an extra property around is cheap — deleting one used by an upgrade path will lose data on a subsequent re-save.

If you do remove one, also delete the test assertion in `XmlDeserialize_LegacyV131Schema_PopulatesLegacyFields` so the test stays in sync.
