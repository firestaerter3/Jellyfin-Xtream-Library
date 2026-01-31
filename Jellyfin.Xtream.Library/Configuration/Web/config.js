const XtreamLibraryConfig = {
    pluginUniqueId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',

    // Cache for loaded categories
    vodCategories: [],
    seriesCategories: [],
    selectedVodCategoryIds: [],
    selectedSeriesCategoryIds: [],

    // Track last clicked checkbox per category type for shift+click range selection
    lastClickedIndex: { vod: null, series: null },

    loadConfig: function () {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(this.pluginUniqueId).then(function (config) {
            document.getElementById('txtBaseUrl').value = config.BaseUrl || '';
            document.getElementById('txtUsername').value = config.Username || '';
            document.getElementById('txtPassword').value = config.Password || '';
            document.getElementById('txtUserAgent').value = config.UserAgent || '';
            document.getElementById('txtLibraryPath').value = config.LibraryPath || '/config/xtream-library';
            document.getElementById('chkSyncMovies').checked = config.SyncMovies !== false;
            document.getElementById('chkSyncSeries').checked = config.SyncSeries !== false;
            document.getElementById('txtSyncInterval').value = config.SyncIntervalMinutes || 60;
            document.getElementById('chkTriggerScan').checked = config.TriggerLibraryScan !== false;
            document.getElementById('chkCleanupOrphans').checked = config.CleanupOrphans !== false;

            // Store selected category IDs
            XtreamLibraryConfig.selectedVodCategoryIds = config.SelectedVodCategoryIds || [];
            XtreamLibraryConfig.selectedSeriesCategoryIds = config.SelectedSeriesCategoryIds || [];

            Dashboard.hideLoadingMsg();
        });

        // Load last sync status
        this.loadSyncStatus();
    },

    saveConfig: function () {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(this.pluginUniqueId).then(function (config) {
            config.BaseUrl = document.getElementById('txtBaseUrl').value.trim().replace(/\/$/, '');
            config.Username = document.getElementById('txtUsername').value.trim();
            config.Password = document.getElementById('txtPassword').value;
            config.UserAgent = document.getElementById('txtUserAgent').value.trim();
            config.LibraryPath = document.getElementById('txtLibraryPath').value.trim();
            config.SyncMovies = document.getElementById('chkSyncMovies').checked;
            config.SyncSeries = document.getElementById('chkSyncSeries').checked;
            config.SyncIntervalMinutes = parseInt(document.getElementById('txtSyncInterval').value) || 60;
            config.TriggerLibraryScan = document.getElementById('chkTriggerScan').checked;
            config.CleanupOrphans = document.getElementById('chkCleanupOrphans').checked;

            // Get selected category IDs from checkboxes
            config.SelectedVodCategoryIds = XtreamLibraryConfig.getSelectedCategoryIds('vod');
            config.SelectedSeriesCategoryIds = XtreamLibraryConfig.getSelectedCategoryIds('series');

            ApiClient.updatePluginConfiguration(XtreamLibraryConfig.pluginUniqueId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    },

    testConnection: function () {
        const statusSpan = document.getElementById('connectionStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Testing...</span>';

        const credentials = {
            BaseUrl: document.getElementById('txtBaseUrl').value.trim().replace(/\/$/, ''),
            Username: document.getElementById('txtUsername').value.trim(),
            Password: document.getElementById('txtPassword').value
        };

        fetch(ApiClient.getUrl('XtreamLibrary/TestConnection'), {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            },
            body: JSON.stringify(credentials)
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            if (data.Success) {
                statusSpan.innerHTML = '<span style="color: green;">' + data.Message + '</span>';
            } else {
                statusSpan.innerHTML = '<span style="color: red;">' + data.Message + '</span>';
            }
        }).catch(function (error) {
            console.error('TestConnection error:', error);
            statusSpan.innerHTML = '<span style="color: red;">Connection failed: ' + (error.message || 'Check console for details') + '</span>';
        });
    },

    runSync: function () {
        const statusSpan = document.getElementById('syncStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Syncing...</span>';

        ApiClient.fetch({
            url: ApiClient.getUrl('XtreamLibrary/Sync'),
            type: 'POST',
            dataType: 'json'
        }).then(function (response) {
            if (response && typeof response.json === 'function') {
                return response.json();
            }
            return response;
        }).then(function (data) {
            if (data.Success) {
                statusSpan.innerHTML = '<span style="color: green;">Sync completed!</span>';
                XtreamLibraryConfig.displaySyncResult(data);
            } else {
                statusSpan.innerHTML = '<span style="color: red;">Sync failed: ' + (data.Error || 'Unknown error') + '</span>';
            }
        }).catch(function (error) {
            console.error('Sync error:', error);
            statusSpan.innerHTML = '<span style="color: red;">Sync failed: ' + (error.message || 'Check console for details') + '</span>';
        });
    },

    loadSyncStatus: function () {
        ApiClient.fetch({
            url: ApiClient.getUrl('XtreamLibrary/Status'),
            type: 'GET',
            dataType: 'json'
        }).then(function (response) {
            if (response && typeof response.json === 'function') {
                return response.json();
            }
            return response;
        }).then(function (data) {
            if (data) {
                XtreamLibraryConfig.displaySyncResult(data);
            }
        }).catch(function () {
            // No previous sync, ignore
        });
    },

    displaySyncResult: function (result) {
        const infoDiv = document.getElementById('lastSyncInfo');
        if (!result) {
            infoDiv.innerHTML = '';
            return;
        }

        const startTime = new Date(result.StartTime).toLocaleString();
        const status = result.Success ? '<span style="color: green;">Success</span>' : '<span style="color: red;">Failed</span>';

        let html = '<div class="fieldDescription">';
        html += '<strong>Last Sync:</strong> ' + startTime + ' - ' + status + '<br/>';
        html += '<strong>Movies:</strong> ' + result.MoviesCreated + ' created, ' + result.MoviesSkipped + ' skipped<br/>';
        html += '<strong>Episodes:</strong> ' + result.EpisodesCreated + ' created, ' + result.EpisodesSkipped + ' skipped<br/>';
        html += '<strong>Orphans Deleted:</strong> ' + result.FilesDeleted;
        if (result.Errors > 0) {
            html += '<br/><span style="color: orange;"><strong>Errors:</strong> ' + result.Errors + '</span>';
        }
        if (result.Error) {
            html += '<br/><span style="color: red;"><strong>Error:</strong> ' + result.Error + '</span>';
        }
        html += '</div>';

        infoDiv.innerHTML = html;
    },

    loadCategories: function () {
        const statusSpan = document.getElementById('categoryLoadStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Loading categories...</span>';

        const self = this;

        // Fetch both VOD and Series categories in parallel
        Promise.all([
            fetch(ApiClient.getUrl('XtreamLibrary/Categories/Vod'), {
                method: 'GET',
                headers: {
                    'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
                }
            }).then(function (r) { return r.ok ? r.json() : Promise.reject(r); }),
            fetch(ApiClient.getUrl('XtreamLibrary/Categories/Series'), {
                method: 'GET',
                headers: {
                    'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
                }
            }).then(function (r) { return r.ok ? r.json() : Promise.reject(r); })
        ]).then(function (results) {
            self.vodCategories = results[0] || [];
            self.seriesCategories = results[1] || [];

            self.renderCategoryList('vod', self.vodCategories, self.selectedVodCategoryIds);
            self.renderCategoryList('series', self.seriesCategories, self.selectedSeriesCategoryIds);

            document.getElementById('vodCategoriesSection').style.display = 'block';
            document.getElementById('seriesCategoriesSection').style.display = 'block';

            statusSpan.innerHTML = '<span style="color: green;">Loaded ' + self.vodCategories.length + ' VOD, ' + self.seriesCategories.length + ' Series categories</span>';
        }).catch(function (error) {
            console.error('Failed to load categories:', error);
            statusSpan.innerHTML = '<span style="color: red;">Failed to load categories. Check credentials and try again.</span>';
        });
    },

    renderCategoryList: function (type, categories, selectedIds) {
        const listId = type === 'vod' ? 'vodCategoryList' : 'seriesCategoryList';
        const container = document.getElementById(listId);

        if (!categories || categories.length === 0) {
            container.innerHTML = '<div class="fieldDescription">No categories found.</div>';
            return;
        }

        let html = '';
        categories.forEach(function (category, index) {
            const isChecked = selectedIds.includes(category.CategoryId) ? 'checked' : '';
            const checkboxId = type + 'Cat_' + category.CategoryId;
            html += '<div class="checkboxContainer">';
            html += '<label class="emby-checkbox-label">';
            html += '<input is="emby-checkbox" type="checkbox" id="' + checkboxId + '" ';
            html += 'data-category-id="' + category.CategoryId + '" data-category-type="' + type + '" ';
            html += 'data-index="' + index + '" ' + isChecked + '/>';
            html += '<span>' + XtreamLibraryConfig.escapeHtml(category.CategoryName) + '</span>';
            html += '</label>';
            html += '</div>';
        });

        container.innerHTML = html;

        // Add shift+click range selection support
        const self = this;
        const checkboxes = container.querySelectorAll('input[type="checkbox"]');
        checkboxes.forEach(function (checkbox) {
            checkbox.addEventListener('click', function (e) {
                const currentIndex = parseInt(checkbox.getAttribute('data-index'));
                const lastIndex = self.lastClickedIndex[type];

                if (e.shiftKey && lastIndex !== null && lastIndex !== currentIndex) {
                    const start = Math.min(lastIndex, currentIndex);
                    const end = Math.max(lastIndex, currentIndex);
                    const newState = checkbox.checked;

                    for (let i = start; i <= end; i++) {
                        checkboxes[i].checked = newState;
                    }
                }

                self.lastClickedIndex[type] = currentIndex;
            });
        });
    },

    getSelectedCategoryIds: function (type) {
        const checkboxes = document.querySelectorAll('input[data-category-type="' + type + '"]:checked');
        const ids = [];
        checkboxes.forEach(function (checkbox) {
            ids.push(parseInt(checkbox.getAttribute('data-category-id')));
        });
        return ids;
    },

    selectAllCategories: function (type) {
        const checkboxes = document.querySelectorAll('input[data-category-type="' + type + '"]');
        checkboxes.forEach(function (checkbox) {
            checkbox.checked = true;
        });
    },

    deselectAllCategories: function (type) {
        const checkboxes = document.querySelectorAll('input[data-category-type="' + type + '"]');
        checkboxes.forEach(function (checkbox) {
            checkbox.checked = false;
        });
    },

    escapeHtml: function (text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
};

// Initialize when DOM is ready
function initXtreamLibraryConfig() {
    const form = document.getElementById('XtreamLibraryConfigForm');
    const btnTest = document.getElementById('btnTestConnection');
    const btnSync = document.getElementById('btnManualSync');
    const btnLoadCategories = document.getElementById('btnLoadCategories');

    if (form) {
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.saveConfig();
            return false;
        });
    }

    if (btnTest) {
        btnTest.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.testConnection();
        });
    }

    if (btnSync) {
        btnSync.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.runSync();
        });
    }

    if (btnLoadCategories) {
        btnLoadCategories.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.loadCategories();
        });
    }

    XtreamLibraryConfig.loadConfig();
}

// Try multiple initialization methods for compatibility
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initXtreamLibraryConfig);
} else {
    initXtreamLibraryConfig();
}
