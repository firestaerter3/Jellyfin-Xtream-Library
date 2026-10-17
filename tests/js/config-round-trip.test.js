// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// Opening the settings page and pressing Save without touching anything must write back exactly
// what was loaded. This drives the real loadConfig and saveConfig against a document stub in which
// every id is an element that keeps whatever value the page puts in it, and compares the saved
// configuration field by field with the one that was loaded.
//
// The fixture sets every field to something other than its default. A field the page loads with
// `|| default` and a fixture that already holds the default would pass whatever the page did.
//
// Both providers use Single folder mode. Multiple mode reads folder rows the page draws itself,
// which this stub does not render; folder-mode.test.js covers that path.

'use strict';

const test = require('node:test');
const assert = require('node:assert');
const { loadConfig } = require('./helpers/config-harness');

function fixture() {
    const provider = (n) => ({
        Name: 'Provider ' + n + ' name',
        IsEnabled: true,
        BaseUrl: 'http://provider' + n + '.example:8080',
        Username: 'user' + n,
        Password: 'pass' + n,
        UserAgent: 'Agent/' + n,
        LibraryPath: '/media/lib' + n,
        SyncMovies: false,
        SyncSeries: false,
        SelectedVodCategoryIds: [n * 10 + 1, n * 10 + 2],
        SelectedSeriesCategoryIds: [n * 10 + 3],
        ExcludedVodStreamIds: [n * 100 + 1],
        ExcludedSeriesIds: [n * 100 + 2],
        MovieFolderMode: 'Single',
        SeriesFolderMode: 'Single',
        MovieCategoriesMode: 'Exclude',
        SeriesCategoriesMode: 'Exclude',
        MovieFolderMappings: '',
        SeriesFolderMappings: '',
        TmdbFolderIdOverrides: 'Movie A=' + n,
        TvdbFolderIdOverrides: 'Show B=' + n,
        CustomTitleRemoveTerms: 'TERM' + n,
        RegexRemovalPatterns: '\\[x' + n + '\\]',
        DownloadArtworkForUnmatched: false,
        FallbackToYearlessLookup: true,
        EnableProactiveMediaInfo: true,
        EnableDispatcharrMode: true,
        DispatcharrBaseUrl: 'http://dispatcharr' + n + '.example:9191',
        GroupMoviesByTmdbId: true,
        DispatcharrApiUser: 'admin' + n,
        DispatcharrApiPass: 'secret' + n,
        EnableIncrementalSync: false,
        FullSyncIntervalDays: 3 + n,
        CleanupOrphans: false,
        OrphanSafetyThreshold: 0.35,
        SmartSkipExisting: false,
        SyncParallelism: 4 + n,
        CategoryBatchSize: 7 + n,
        RequestDelayMs: 0,
        MaxRetries: 5 + n,
        RetryDelayMs: 2500 + n,
        TimeoutSeconds: 90 + n,
    });

    return {
        Providers: [provider(1), provider(2)],
        SyncIntervalMinutes: 45,
        TriggerLibraryScan: true,
        EnableMetadataLookup: false,
        UseBetaChannel: true,
        MetadataParallelism: 6,
        SyncScheduleType: 'Daily',
        SyncDailyHour: 0,
        SyncDailyMinute: 30,
        EnableLiveTv: true,
        EnableNativeTuner: true,
        EnableEpg: false,
        LiveTvOutputFormat: 'm3u8',
        IncludeAdultChannels: true,
        M3UCacheMinutes: 20,
        EpgCacheMinutes: 40,
        EpgDaysToFetch: 4,
        EpgParallelism: 8,
        SelectedLiveCategoryIds: [7, 8],
        ExcludedLiveStreamIds: [700],
        LiveChannelMode: 'ExcludeSelected',
        EnableChannelNameCleaning: false,
        LiveTvNumberByCategory: true,
        ChannelRemoveTerms: 'HD\nFHD',
        ChannelOverrides: '123=Name',
        LiveTvEndpointAllowedIps: '127.0.0.1\n::1',
        PublishLiveTvPlaylists: true,
        EnableCatchup: true,
        CatchupDays: 12,
        EmitCredentialedCatchupSource: true,
        ShowCatchupInJellyfin: true,
        CatchupBlockMinutes: 0,
        CatchupTimeShiftMinutes: -60,
        // Not on the page at all; it has to survive because saveConfig starts from the stored copy.
        SomeFieldThePageNeverShows: 'kept',
    };
}

/** A document in which every id exists and holds what the page writes into it. */
function installPage() {
    const elements = {};
    const element = () => ({
        style: {},
        value: '',
        innerHTML: '',
        textContent: '',
        checked: false,
        classList: { add() {}, remove() {}, toggle() {}, contains: () => false },
        querySelector: () => null,
        querySelectorAll: () => [],
        getAttribute: () => null,
        setAttribute() {},
        addEventListener() {},
        appendChild() {},
    });
    const radios = ['IncludeAll', 'Custom', 'ExcludeSelected'].map((value) =>
        Object.assign(element(), { value, name: 'LiveChannelMode' }));

    global.document = {
        readyState: 'loading',
        getElementById: (id) => (elements[id] = elements[id] || element()),
        getElementsByName: (name) => (name === 'LiveChannelMode' ? radios : []),
        querySelector: (selector) =>
            selector === 'input[name="LiveChannelMode"]:checked' ? radios.find((r) => r.checked) || null : null,
        // Category checkboxes are drawn after a provider fetch, which never happens here.
        querySelectorAll: () => [],
        addEventListener() {},
        createElement: element,
    };
    return { elements, radios };
}

async function drain() {
    for (let i = 0; i < 10; i++) {
        await new Promise((resolve) => setImmediate(resolve));
    }
}

/** Runs load then save and returns what save handed to Jellyfin, plus the page object. */
async function roundTrip(stored, betweenLoadAndSave) {
    const saved = [];
    global.ApiClient = {
        getPluginConfiguration: () => Promise.resolve(JSON.parse(JSON.stringify(stored))),
        updatePluginConfiguration: (id, config) => {
            saved.push(JSON.parse(JSON.stringify(config)));
            return Promise.resolve();
        },
        getUrl: (p) => '/' + p,
        accessToken: () => 'token',
    };
    global.Dashboard = {
        showLoadingMsg() {},
        hideLoadingMsg() {},
        alert() {},
        processPluginConfigurationUpdateResult() {},
    };
    global.fetch = () => new Promise(() => {});

    const page = installPage();
    const cfg = loadConfig();
    for (const name of ['loadSyncStatus', 'checkRunningSync', 'loadDashboard', 'loadVodCategories',
        'loadSeriesCategories', 'loadLiveCategories', 'updateLiveTvUrls']) {
        cfg[name] = () => {};
    }

    cfg.loadConfig();
    await drain();
    if (betweenLoadAndSave) {
        betweenLoadAndSave(cfg, page);
    }
    cfg.saveConfig();
    await drain();

    assert.strictEqual(saved.length, 1, 'saveConfig did not reach updatePluginConfiguration');
    return { saved: saved[0], cfg, page };
}

test.afterEach(() => {
    delete global.document;
    delete global.ApiClient;
    delete global.Dashboard;
    delete global.fetch;
});

test('saving without changes writes back exactly what was loaded', async () => {
    const stored = fixture();
    const { saved } = await roundTrip(stored);

    const changed = [];
    for (const key of Object.keys(stored)) {
        if (key === 'Providers') continue;
        if (JSON.stringify(saved[key]) !== JSON.stringify(stored[key])) {
            changed.push(`${key}: ${JSON.stringify(stored[key])} -> ${JSON.stringify(saved[key])}`);
        }
    }
    assert.strictEqual(saved.Providers.length, 2);
    for (let i = 0; i < 2; i++) {
        for (const key of Object.keys(stored.Providers[i])) {
            if (JSON.stringify(saved.Providers[i][key]) !== JSON.stringify(stored.Providers[i][key])) {
                changed.push(`Provider ${i + 1} ${key}: ${JSON.stringify(stored.Providers[i][key])} -> `
                    + JSON.stringify(saved.Providers[i][key]));
            }
        }
    }
    assert.deepStrictEqual(changed, [], 'changed on a save without edits');
});

test('every combination of native tuner and published playlists survives a save', async () => {
    for (const native of [true, false]) {
        for (const publish of [true, false]) {
            const stored = Object.assign(fixture(), { EnableNativeTuner: native, PublishLiveTvPlaylists: publish });
            const { saved } = await roundTrip(stored);
            assert.strictEqual(saved.EnableNativeTuner, native, `native=${native} publish=${publish}`);
            assert.strictEqual(saved.PublishLiveTvPlaylists, publish, `native=${native} publish=${publish}`);
        }
    }
});

test('Live TV and catch-up settings survive a save while their switch is off', async () => {
    const stored = Object.assign(fixture(), { EnableLiveTv: false, EnableCatchup: false });
    const { saved } = await roundTrip(stored);
    for (const key of ['EnableNativeTuner', 'PublishLiveTvPlaylists', 'CatchupDays', 'ShowCatchupInJellyfin',
        'EmitCredentialedCatchupSource', 'CatchupBlockMinutes', 'CatchupTimeShiftMinutes', 'LiveChannelMode']) {
        assert.deepStrictEqual(saved[key], stored[key], key + ' changed while its section was switched off');
    }
});

test('a drawn category list is still read, so clearing every tick is saved', async () => {
    // The fallback above only covers a list that was never drawn. Once it is drawn, what the boxes
    // say is the selection, including nothing ticked.
    const stored = fixture();
    const { saved } = await roundTrip(stored, () => {
        const unticked = { getAttribute: (a) => (a === 'data-category-id' ? '11' : null) };
        document.querySelectorAll = (selector) =>
            selector === 'input[data-category-type="vod"]' ? [unticked] : [];
    });
    assert.deepStrictEqual(saved.Providers[0].SelectedVodCategoryIds, []);
    assert.deepStrictEqual(saved.Providers[0].SelectedSeriesCategoryIds, [13]);
});

// Settings page behaviour (round 2, step B). These use the same page as the round trips above.

test('picking the M3U tuner turns publishing on; picking native leaves it as it was', async () => {
    for (const publish of [true, false]) {
        const { saved } = await roundTrip(Object.assign(fixture(), { EnableNativeTuner: true, PublishLiveTvPlaylists: publish }),
            (cfg) => cfg.chooseTuner('m3u'));
        assert.strictEqual(saved.EnableNativeTuner, false);
        assert.strictEqual(saved.PublishLiveTvPlaylists, true, 'the M3U tuner reads the published playlist');
    }
    for (const publish of [true, false]) {
        const { saved } = await roundTrip(Object.assign(fixture(), { EnableNativeTuner: false, PublishLiveTvPlaylists: publish }),
            (cfg) => cfg.chooseTuner('native'));
        assert.strictEqual(saved.EnableNativeTuner, true);
        assert.strictEqual(saved.PublishLiveTvPlaylists, publish, 'other apps may still use the playlists');
    }
});

test('loading shows the saved tuner and warns, without changing anything, when the M3U tuner has no playlist', async () => {
    const { saved, page } = await roundTrip(Object.assign(fixture(), { EnableNativeTuner: false, PublishLiveTvPlaylists: false }));
    assert.strictEqual(page.elements.radTunerM3u.checked, true);
    assert.strictEqual(page.elements.radTunerNative.checked, false);
    assert.strictEqual(page.elements.tunerChoiceWarning.style.display, '');
    assert.strictEqual(saved.PublishLiveTvPlaylists, false);

    const native = await roundTrip(fixture());
    assert.strictEqual(native.page.elements.radTunerNative.checked, true);
    assert.strictEqual(native.page.elements.tunerChoiceWarning.style.display, 'none');
});

test('switched-off Live TV and catch-up hide what depends on them', async () => {
    const off = await roundTrip(Object.assign(fixture(), { EnableLiveTv: false, EnableCatchup: false }));
    assert.strictEqual(off.page.elements.liveTvDependentTop.style.display, 'none');
    assert.strictEqual(off.page.elements.liveTvDependentRest.style.display, 'none');
    assert.strictEqual(off.page.elements.catchupDependent.style.display, 'none');

    const on = await roundTrip(fixture());
    assert.strictEqual(on.page.elements.liveTvDependentTop.style.display, '');
    assert.strictEqual(on.page.elements.liveTvDependentRest.style.display, '');
    assert.strictEqual(on.page.elements.catchupDependent.style.display, '');
});

test('the provider picker shows only on the tabs that belong to one provider', async () => {
    const { cfg, page } = await roundTrip(fixture());
    for (const [tab, shown] of [['dashboard', false], ['provider', true], ['movies', true], ['series', true],
        ['livetv', false], ['general', false]]) {
        cfg.switchTab(tab);
        assert.strictEqual(page.elements.providerBar.style.display, shown ? '' : 'none', tab);
    }
});

test('the dashboard asks for a provider only while none is set up', async () => {
    const empty = fixture();
    empty.Providers = [Object.assign(empty.Providers[0], { BaseUrl: '' })];
    const { page } = await roundTrip(empty);
    assert.strictEqual(page.elements.emptyStateCard.style.display, '');

    const configured = await roundTrip(fixture());
    assert.strictEqual(configured.page.elements.emptyStateCard.style.display, 'none');
});

test('the delete question names the saved provider and folder, and what is lost', async () => {
    const { cfg, page } = await roundTrip(fixture());
    const saved = fixture().Providers[0];
    let message = cfg.cleanConfirmMessage('Series', saved);
    assert.match(message, /"Provider 1 name"/);
    assert.match(message, /\/media\/lib1\/Series\?/);
    assert.match(message, /watched status/);
    assert.doesNotMatch(message, /not saved/);

    // The server deletes under the saved path, so an unsaved edit in the field must not be named
    // as the folder that goes.
    page.elements.txtLibraryPath.value = '/media/elsewhere';
    message = cfg.cleanConfirmMessage('Series', saved);
    assert.match(message, /in \/media\/lib1\/Series\?/);
    assert.match(message, /not saved/);
});

test('the duplicate tuner warning shows only with the native tuner chosen', async () => {
    const { cfg, page } = await roundTrip(fixture());
    cfg.m3uTunerOnPluginPlaylist = true;
    cfg.updateTunerHint();
    assert.strictEqual(page.elements.duplicateTunerWarning.style.display, '');

    cfg.chooseTuner('m3u');
    assert.strictEqual(page.elements.duplicateTunerWarning.style.display, 'none');

    cfg.chooseTuner('native');
    cfg.m3uTunerOnPluginPlaylist = false;
    cfg.updateTunerHint();
    assert.strictEqual(page.elements.duplicateTunerWarning.style.display, 'none');
});
