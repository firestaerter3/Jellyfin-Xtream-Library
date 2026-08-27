// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// Test harness for Jellyfin.Xtream.Library/Configuration/Web/config.js.
//
// These tests live here rather than beside config.js because the csproj embeds
// Configuration/Web/** wholesale, so anything dropped in that folder ships inside the DLL.
//
// config.js is a browser script, not a module. It guards its own bootstrap on `document` being
// defined and exports the object literal when `module` exists, so requiring it from Node yields
// the config object and nothing else. The functions look `document` up at call time, so a stub
// installed per test is enough for the DOM-touching ones - no jsdom needed.

'use strict';

const path = require('node:path');

const CONFIG_PATH = path.join(
    __dirname, '..', '..', '..',
    'Jellyfin.Xtream.Library', 'Configuration', 'Web', 'config.js');

/**
 * Loads a fresh copy of the config object, bypassing the require cache so tests that mutate
 * state (folder definitions, providers) cannot leak into each other.
 */
function loadConfig() {
    delete require.cache[require.resolve(CONFIG_PATH)];
    return require(CONFIG_PATH);
}

/** A DOM element stub. Override only what a given test actually reads. */
function element(overrides) {
    return Object.assign({
        style: {},
        value: '',
        innerHTML: '',
        checked: false,
        querySelector: () => null,
        querySelectorAll: () => [],
        getAttribute: () => null,
        setAttribute: () => {},
        addEventListener: () => {},
    }, overrides);
}

/**
 * A folder row as renderFolderList would have drawn it: a name input plus the ticked category
 * checkboxes. This is the shape updateFolderDefinitionsFromUI reads back.
 */
function folderItem(name, categoryIds) {
    return element({
        querySelector: (selector) =>
            selector === '.folder-name-input' ? element({ value: name }) : null,
        querySelectorAll: () => categoryIds.map((id) =>
            element({ getAttribute: (attr) => (attr === 'data-category-id' ? String(id) : null) })),
    });
}

/**
 * Installs a document stub backed by the given id -> element map, and returns a restore function.
 *
 * `selectors` optionally maps a CSS selector string to what the document-level query should
 * return, for the functions that reach for checkboxes by attribute rather than by id. A selector
 * with no entry resolves to nothing, which is the "not rendered yet" state.
 */
function withDocument(elementsById, selectors = {}) {
    const previous = global.document;
    global.document = {
        readyState: 'complete',
        getElementById: (id) => elementsById[id] || null,
        querySelectorAll: (selector) => selectors[selector] || [],
        querySelector: (selector) => (selectors[selector] || [])[0] || null,
        addEventListener: () => {},
    };
    return () => {
        if (previous === undefined) {
            delete global.document;
        } else {
            global.document = previous;
        }
    };
}

module.exports = { CONFIG_PATH, loadConfig, element, folderItem, withDocument };
