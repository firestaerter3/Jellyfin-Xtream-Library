// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #184. The Remove button refused the only provider ("At least one provider is required"),
// so switching to another provider meant overwriting the old one field by field. The last provider
// can now be removed too; it is replaced by an empty one that keeps the library path.
//
// These tests stub the rendering and check the provider list the page is left with. They say
// nothing about what the browser draws.

'use strict';

const test = require('node:test');
const assert = require('node:assert');
const { loadConfig, element, withDocument } = require('./helpers/config-harness');

/** A config object with the given providers, rendering stubbed out. */
function withProviders(providers, active) {
    const config = loadConfig();
    config.providers = providers;
    config.activeProviderIndex = active;
    config.renderProviderSelector = () => {};
    config.loaded = [];
    config.loadProviderIntoUI = (index) => {
        config.loaded.push(index);
        config.activeProviderIndex = index;
    };
    return config;
}

test('the only provider can be removed and is replaced by an empty one', () => {
    const config = withProviders([{ Name: 'Old', BaseUrl: 'http://old.test', Username: 'u', Password: 'p', LibraryPath: '/media/xtream', SelectedVodCategoryIds: [1, 2] }], 0);

    config.removeActiveProvider();

    assert.strictEqual(config.providers.length, 1);
    const fresh = config.providers[0];
    assert.strictEqual(fresh.BaseUrl, '');
    assert.strictEqual(fresh.Username, '');
    assert.strictEqual(fresh.Password, '');
    assert.deepStrictEqual(fresh.SelectedVodCategoryIds, []);
    assert.strictEqual(fresh.Name, 'Provider 1');
    assert.strictEqual(fresh.LibraryPath, '/media/xtream', 'the Jellyfin libraries already point at this path');
    assert.deepStrictEqual(config.loaded, [0]);
});

test('removing one of several providers just removes it', () => {
    const config = withProviders([{ Name: 'A', BaseUrl: 'http://a.test' }, { Name: 'B', BaseUrl: 'http://b.test' }], 1);

    config.removeActiveProvider();

    assert.deepStrictEqual(config.providers.map(p => p.Name), ['A']);
    assert.strictEqual(config.providers[0].BaseUrl, 'http://a.test');
    assert.deepStrictEqual(config.loaded, [0]);
});

test('the question says what stays behind only when the last provider goes', () => {
    const last = withProviders([{ Name: 'Old' }], 0).removeProviderPrompt();
    assert.match(last, /^Remove "Old"\?/);
    assert.match(last, /click Save/);
    assert.match(last, /Delete Movies\/Series Library/);
    assert.match(last, /Orphan Safety Threshold, and a library of ten files or fewer has no such limit/);
    assert.match(last, /Channel Overrides/);

    const one = withProviders([{ Name: 'A' }, { Name: 'B' }], 0).removeProviderPrompt();
    assert.strictEqual(one, 'Remove "A"? This cannot be undone.');
});

// CodeRabbit on #189: the category lists of the provider that was shown stayed in memory, and a
// request still running for it could fill them after the switch, so its categories could end up
// in the next provider's selection.
test('a category answer for the previous provider is dropped', async () => {
    const config = loadConfig();
    config.providers = [{ Name: 'Old' }];
    config.activeProviderIndex = 0;
    config.categoryLoadGeneration = 1;
    config.vodCategories = [];

    let answer;
    const previous = { fetch: global.fetch, ApiClient: global.ApiClient };
    global.ApiClient = { getUrl: (path) => path, accessToken: () => 'token' };
    global.fetch = () => new Promise((resolve) => { answer = resolve; });
    const restore = withDocument({ vodCategoryLoadStatus: element(), selMovieFolderMode: element({ value: 'Single' }) });
    let rendered = false;
    config.renderCategoryList = () => { rendered = true; };
    try {
        config.loadVodCategories();

        // The provider changes while the request is out, as loadProviderIntoUI does.
        config.categoryLoadGeneration++;
        config.vodCategories = [];

        answer({ ok: true, json: () => [{ CategoryId: 7, CategoryName: 'Old provider' }] });
        await new Promise((resolve) => setImmediate(resolve));
    } finally {
        restore();
        global.fetch = previous.fetch;
        global.ApiClient = previous.ApiClient;
    }

    assert.deepStrictEqual(config.vodCategories, []);
    assert.strictEqual(rendered, false);
});
