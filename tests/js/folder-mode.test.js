// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// The config page owns which categories get synced and into which folders, and it produced three
// distinct routes into "no categories selected" - which used to mean "sync the provider's entire
// catalogue into the library root" (GitHub #78). None of it had any test coverage.

'use strict';

const test = require('node:test');
const assert = require('node:assert');
const { loadConfig, element, folderItem, withDocument } = require('./helpers/config-harness');

test('folder mappings round-trip through the wire format', async (t) => {
    await t.test('a parsed mapping rebuilds to the same string', () => {
        const config = loadConfig();
        const wire = 'Kids=10,15\nDocumentary=30';

        assert.strictEqual(config.buildFolderMappings(config.parseFolderMappings(wire)), wire);
    });

    await t.test('one category may live in several folders', () => {
        const config = loadConfig();

        assert.deepStrictEqual(
            config.parseFolderMappings('Kids=10\nFamily=10'),
            [{ name: 'Kids', categoryIds: [10] }, { name: 'Family', categoryIds: [10] }]);
    });

    await t.test('a folder holding no categories is dropped, which is what creates the #78 state', () => {
        const config = loadConfig();

        // The user sees a named folder on screen; nothing of it survives the save. That is how a
        // provider ends up in Multiple folder mode with an empty mappings string.
        assert.strictEqual(
            config.buildFolderMappings([{ name: 'Empty', categoryIds: [] }]),
            '');
    });

    await t.test('an unparseable line is skipped rather than poisoning the rest', () => {
        const config = loadConfig();

        assert.deepStrictEqual(
            config.parseFolderMappings('nonsense\nKids=10'),
            [{ name: 'Kids', categoryIds: [10] }]);
    });
});

test('getAllCategoryIdsFromFolders returns the union across folders', async (t) => {
    await t.test('deduplicates a category assigned to two folders', () => {
        const config = loadConfig();
        config.vodFolderDefinitions = [
            { name: 'Kids', categoryIds: [10, 15] },
            { name: 'Family', categoryIds: [10, 20] },
        ];

        assert.deepStrictEqual(config.getAllCategoryIdsFromFolders('vod'), [10, 15, 20]);
    });

    await t.test('no folder holding a category yields an empty union', () => {
        const config = loadConfig();
        config.vodFolderDefinitions = [{ name: 'Empty', categoryIds: [] }];

        assert.deepStrictEqual(config.getAllCategoryIdsFromFolders('vod'), []);
    });
});

test('findEmptyFolderModeContent guards the save', async (t) => {
    const provider = (overrides) => Object.assign({
        Name: 'A',
        SyncMovies: true,
        SyncSeries: true,
        MovieFolderMode: 'Single',
        MovieFolderMappings: '',
        SeriesFolderMode: 'Single',
        SeriesFolderMappings: '',
    }, overrides);

    const check = (providers) => {
        const config = loadConfig();
        config.providers = providers;
        config.activeProviderIndex = 0;
        return config.findEmptyFolderModeContent();
    };

    await t.test('Multiple mode with no categories assigned is refused', () => {
        assert.strictEqual(
            check([provider({ MovieFolderMode: 'Multiple', MovieFolderMappings: '' })]),
            'A / Movies');
    });

    await t.test('Multiple mode with categories assigned is fine', () => {
        assert.strictEqual(
            check([provider({ MovieFolderMode: 'Multiple', MovieFolderMappings: 'Kids=10' })]),
            null);
    });

    await t.test('Single mode with nothing selected is fine, since that still means sync everything', () => {
        assert.strictEqual(check([provider({})]), null);
    });

    await t.test('the series side is checked too', () => {
        assert.strictEqual(
            check([provider({ SeriesFolderMode: 'Multiple', SeriesFolderMappings: '' })]),
            'A / Series');
    });

    await t.test('a provider that is not on screen is still checked', () => {
        // Switching providers flushes the outgoing one into the array, so a broken config can be
        // sitting there unsaved while a different provider is displayed.
        assert.strictEqual(
            check([
                provider({ Name: 'A' }),
                provider({ Name: 'B', MovieFolderMode: 'Multiple', MovieFolderMappings: '' }),
            ]),
            'B / Movies');
    });

    await t.test('content that is switched off does not block the save', () => {
        // Otherwise turning Movies off while its folder mode happens to be Multiple locks the user
        // out of saving anything at all.
        assert.strictEqual(
            check([provider({
                SyncMovies: false,
                MovieFolderMode: 'Multiple',
                MovieFolderMappings: '',
            })]),
            null);
    });

    await t.test('a provider from an older UI without the Sync flags is still checked', () => {
        assert.strictEqual(
            check([{ Name: 'A', MovieFolderMode: 'Multiple', MovieFolderMappings: '', SeriesFolderMode: 'Single' }]),
            'A / Movies');
    });

    await t.test('an unnamed provider is identified by position', () => {
        assert.strictEqual(
            check([provider({ Name: '', MovieFolderMode: 'Multiple', MovieFolderMappings: '' })]),
            'Provider 1 / Movies');
    });
});

test('updateFolderDefinitionsFromUI does not wipe a configuration it never rendered', async (t) => {
    await t.test('an unrendered folder list leaves the loaded definitions alone', () => {
        const config = loadConfig();
        config.vodFolderDefinitions = [{ name: 'Kids', categoryIds: [10] }];

        // What renderFolderList leaves behind when the categories have not loaded: the placeholder
        // div, and no .folder-item nodes at all.
        const restore = withDocument({ vodFolderList: element({ querySelectorAll: () => [] }) });
        try {
            config.updateFolderDefinitionsFromUI('vod');
        } finally {
            restore();
        }

        assert.deepStrictEqual(
            config.vodFolderDefinitions,
            [{ name: 'Kids', categoryIds: [10] }],
            'a failed or pending category fetch must not destroy the saved folder configuration');
    });

    await t.test('a genuinely emptied folder list is still honoured', () => {
        const config = loadConfig();
        config.vodFolderDefinitions = [];

        const restore = withDocument({ vodFolderList: element({ querySelectorAll: () => [] }) });
        try {
            config.updateFolderDefinitionsFromUI('vod');
        } finally {
            restore();
        }

        assert.deepStrictEqual(config.vodFolderDefinitions, []);
    });

    await t.test('a rendered folder list is read back from the DOM', () => {
        const config = loadConfig();
        config.vodFolderDefinitions = [{ name: 'Stale', categoryIds: [99] }];

        const restore = withDocument({
            vodFolderList: element({
                querySelectorAll: () => [folderItem('Kids', [10, 15]), folderItem('Docs', [30])],
            }),
        });
        try {
            config.updateFolderDefinitionsFromUI('vod');
        } finally {
            restore();
        }

        assert.deepStrictEqual(config.vodFolderDefinitions, [
            { name: 'Kids', categoryIds: [10, 15] },
            { name: 'Docs', categoryIds: [30] },
        ]);
    });

    await t.test('a folder with no name is dropped, as before', () => {
        const config = loadConfig();
        config.vodFolderDefinitions = [];

        const restore = withDocument({
            vodFolderList: element({
                querySelectorAll: () => [folderItem('   ', [10]), folderItem('Kids', [15])],
            }),
        });
        try {
            config.updateFolderDefinitionsFromUI('vod');
        } finally {
            restore();
        }

        assert.deepStrictEqual(config.vodFolderDefinitions, [{ name: 'Kids', categoryIds: [15] }]);
    });
});

test('switching from Multiple to Single folder mode repaints the flat category list', async (t) => {
    const runSwitch = (mode) => {
        const config = loadConfig();
        config.vodCategories = [{ CategoryId: 10, CategoryName: 'Kids' }];
        config.selectedVodCategoryIds = [10];

        const rendered = [];
        config.renderCategoryList = (type, categories, selectedIds) =>
            rendered.push({ type, categories, selectedIds });
        config.renderFolderList = () => {};

        const restore = withDocument({
            selMovieFolderMode: element({ value: mode }),
            movieCategoriesModeContainer: element({}),
            vodSingleFolderSection: element({}),
            vodMultiFolderSection: element({}),
        });
        try {
            config.updateFolderModeVisibility('vod');
        } finally {
            restore();
        }

        return rendered;
    };

    await t.test('the list is rendered with the previously selected categories', () => {
        // Multiple folder mode never paints this list, so without the repaint the container is
        // empty and getSelectedCategoryIds returns [] - which means sync everything.
        const rendered = runSwitch('Single');

        assert.strictEqual(rendered.length, 1);
        assert.strictEqual(rendered[0].type, 'vod');
        assert.deepStrictEqual(rendered[0].selectedIds, [10],
            'the category choices should survive the switch, not be silently dropped');
    });

    await t.test('Multiple folder mode does not paint the flat list', () => {
        assert.deepStrictEqual(runSwitch('Multiple'), []);
    });
});

// GitHub #81. In Multiple folder mode the user's category choices live in the folder
// assignments, not in the flat list, and a config last saved before the #78 fix carries an
// empty SelectedVodCategoryIds. Seeding the repaint from that field alone paints every box
// unticked over a real selection, and saving then means "sync everything".
//
// The issue blamed emby-checkbox for stripping the checked attribute during upgrade. It does
// not: measured against the real component on Jellyfin 10.11, checked survives insertion on
// every path, including from inside a change dispatch. The bug is the source field.
test('switching to Single falls back to the folder assignments when the flat selection is empty', async (t) => {
    const runSwitch = (options) => {
        const config = loadConfig();
        config.vodCategories = [
            { CategoryId: 10, CategoryName: 'Kids' },
            { CategoryId: 15, CategoryName: 'Documentary' },
        ];
        config.selectedVodCategoryIds = options.selected;
        config.vodFolderDefinitions = options.folders;

        const rendered = [];
        config.renderCategoryList = (type, categories, selectedIds) =>
            rendered.push({ type, categories, selectedIds });
        config.renderFolderList = () => {};

        // No vodFolderList entry: updateFolderDefinitionsFromUI returns early on a null
        // container, so the union comes straight from vodFolderDefinitions.
        const restore = withDocument({
            selMovieFolderMode: element({ value: 'Single' }),
            movieCategoriesModeContainer: element({}),
            vodSingleFolderSection: element({}),
            vodMultiFolderSection: element({}),
        });
        try {
            config.updateFolderModeVisibility('vod', options.fromModeSwitch);
        } finally {
            restore();
        }

        return { rendered, config };
    };

    await t.test('an empty selection is seeded from the folders the user assigned', () => {
        const { rendered } = runSwitch({
            selected: [],
            folders: [{ name: 'Kids', categoryIds: [10, 15] }],
            fromModeSwitch: true,
        });

        assert.deepStrictEqual(rendered[0].selectedIds, [10, 15],
            'the folder assignments are the selection the user can actually see on screen');
    });

    await t.test('the seeded ids are written back so a later repaint agrees', () => {
        // loadVodCategories repaints from selectedVodCategoryIds when its fetch resolves.
        const { config } = runSwitch({
            selected: [],
            folders: [{ name: 'Kids', categoryIds: [10] }],
            fromModeSwitch: true,
        });

        assert.deepStrictEqual(config.selectedVodCategoryIds, [10]);
    });

    await t.test('a real selection is never overridden by the folder assignments', () => {
        const { rendered } = runSwitch({
            selected: [15],
            folders: [{ name: 'Kids', categoryIds: [10] }],
            fromModeSwitch: true,
        });

        assert.deepStrictEqual(rendered[0].selectedIds, [15]);
    });

    await t.test('nothing selected and no folders still means sync everything', () => {
        const { rendered } = runSwitch({ selected: [], folders: [], fromModeSwitch: true });

        assert.deepStrictEqual(rendered[0].selectedIds, []);
    });

    await t.test('the page-load path does not seed, since its render is discarded anyway', () => {
        // loadProviderIntoUI calls this before the folder UI exists, then blanks the container.
        const { rendered, config } = runSwitch({
            selected: [],
            folders: [{ name: 'Kids', categoryIds: [10] }],
            fromModeSwitch: undefined,
        });

        assert.deepStrictEqual(rendered[0].selectedIds, []);
        assert.deepStrictEqual(config.selectedVodCategoryIds, []);
    });

    await t.test('the series side behaves the same', () => {
        const config = loadConfig();
        config.seriesCategories = [{ CategoryId: 20, CategoryName: 'Drama' }];
        config.selectedSeriesCategoryIds = [];
        config.seriesFolderDefinitions = [{ name: 'Drama', categoryIds: [20] }];

        const rendered = [];
        config.renderCategoryList = (type, categories, selectedIds) =>
            rendered.push({ type, categories, selectedIds });
        config.renderFolderList = () => {};

        const restore = withDocument({
            selSeriesFolderMode: element({ value: 'Single' }),
            seriesCategoriesModeContainer: element({}),
            seriesSingleFolderSection: element({}),
            seriesMultiFolderSection: element({}),
        });
        try {
            config.updateFolderModeVisibility('series', true);
        } finally {
            restore();
        }

        assert.deepStrictEqual(rendered[0].selectedIds, [20]);
        assert.deepStrictEqual(config.selectedSeriesCategoryIds, [20]);
    });
});
