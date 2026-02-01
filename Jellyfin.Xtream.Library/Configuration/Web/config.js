const XtreamLibraryConfig = {
    pluginUniqueId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',

    // Cache for loaded categories
    vodCategories: [],
    seriesCategories: [],
    selectedVodCategoryIds: [],
    selectedSeriesCategoryIds: [],

    // Track last clicked checkbox per category type for shift+click range selection
    lastClickedIndex: { vod: null, series: null },

    // Tab switching
    switchTab: function (tabName) {
        // Update tab buttons
        document.querySelectorAll('.xtream-tab').forEach(function (tab) {
            tab.classList.remove('active');
            if (tab.getAttribute('data-tab') === tabName) {
                tab.classList.add('active');
            }
        });

        // Update tab content
        document.querySelectorAll('.xtream-tab-content').forEach(function (content) {
            content.classList.remove('active');
        });
        document.getElementById('tab-' + tabName).classList.add('active');
    },

    loadConfig: function () {
        Dashboard.showLoadingMsg();

        const self = this;

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

            // Folder ID overrides
            document.getElementById('txtTmdbFolderIdOverrides').value = config.TmdbFolderIdOverrides || '';
            document.getElementById('txtTvdbFolderIdOverrides').value = config.TvdbFolderIdOverrides || '';

            // Metadata lookup
            document.getElementById('chkEnableMetadataLookup').checked = config.EnableMetadataLookup || false;

            // Artwork download for unmatched
            document.getElementById('chkDownloadArtworkForUnmatched').checked = config.DownloadArtworkForUnmatched !== false;

            Dashboard.hideLoadingMsg();

            // Auto-load categories if credentials are configured
            if (config.BaseUrl && config.Username) {
                self.loadVodCategories();
                self.loadSeriesCategories();
            }
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

            // Folder ID overrides
            config.TmdbFolderIdOverrides = document.getElementById('txtTmdbFolderIdOverrides').value;
            config.TvdbFolderIdOverrides = document.getElementById('txtTvdbFolderIdOverrides').value;

            // Metadata lookup
            config.EnableMetadataLookup = document.getElementById('chkEnableMetadataLookup').checked;

            // Artwork download for unmatched
            config.DownloadArtworkForUnmatched = document.getElementById('chkDownloadArtworkForUnmatched').checked;

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

    // Progress polling interval handle
    progressInterval: null,
    isSyncing: false,

    runSync: function () {
        const statusSpan = document.getElementById('syncStatus');
        const syncBtn = document.getElementById('btnManualSync');
        const self = this;

        // Update button to Cancel state
        self.isSyncing = true;
        syncBtn.querySelector('span').textContent = 'Cancel Sync';
        syncBtn.style.background = '#c0392b';

        // Start progress polling
        self.startProgressPolling();

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
            self.stopProgressPolling();
            self.resetSyncButton();
            if (data.Success) {
                statusSpan.innerHTML = '<span style="color: green;">Sync completed!</span>';
                self.displaySyncResult(data);
            } else {
                const errorMsg = data.Error || 'Unknown error';
                if (errorMsg.toLowerCase().includes('cancel')) {
                    statusSpan.innerHTML = '<span style="color: orange;">Sync was cancelled.</span>';
                } else {
                    statusSpan.innerHTML = '<span style="color: red;">Sync failed: ' + errorMsg + '</span>';
                }
            }
        }).catch(function (error) {
            self.stopProgressPolling();
            self.resetSyncButton();
            console.error('Sync error:', error);
            statusSpan.innerHTML = '<span style="color: red;">Sync failed: ' + (error.message || 'Check console for details') + '</span>';
        });
    },

    cancelSync: function () {
        const statusSpan = document.getElementById('syncStatus');
        const self = this;

        statusSpan.innerHTML = '<span style="color: orange;">Cancelling sync...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/Cancel'), {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            // Button will reset when sync actually finishes
        }).catch(function (error) {
            console.error('Cancel error:', error);
        });
    },

    resetSyncButton: function () {
        const syncBtn = document.getElementById('btnManualSync');
        this.isSyncing = false;
        syncBtn.querySelector('span').textContent = 'Run Sync Now';
        syncBtn.style.background = '';
    },

    startProgressPolling: function () {
        const self = this;
        const statusSpan = document.getElementById('syncStatus');

        // Initial display
        statusSpan.innerHTML = '<span style="color: orange;">Starting sync...</span>';

        // Poll every 500ms
        self.progressInterval = setInterval(function () {
            fetch(ApiClient.getUrl('XtreamLibrary/Progress'), {
                method: 'GET',
                headers: {
                    'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
                }
            }).then(function (r) {
                return r.ok ? r.json() : null;
            }).then(function (progress) {
                if (progress && progress.IsRunning) {
                    self.displayProgress(progress);
                }
            }).catch(function () {
                // Ignore polling errors
            });
        }, 500);
    },

    stopProgressPolling: function () {
        if (this.progressInterval) {
            clearInterval(this.progressInterval);
            this.progressInterval = null;
        }
    },

    displayProgress: function (progress) {
        const statusSpan = document.getElementById('syncStatus');
        let html = '<span style="color: orange;">';

        // Phase and current category
        html += progress.Phase;
        if (progress.CurrentItem) {
            html += ': ' + this.escapeHtml(progress.CurrentItem);
        }

        // Category progress
        if (progress.TotalCategories > 0) {
            html += '<br/>Categories: ' + progress.CategoriesProcessed + '/' + progress.TotalCategories;
        }

        // Item progress within current category
        if (progress.TotalItems > 0) {
            html += ' | Items: ' + progress.ItemsProcessed + '/' + progress.TotalItems;
        }

        // Created counts
        const created = [];
        if (progress.MoviesCreated > 0) {
            created.push(progress.MoviesCreated + ' movies');
        }
        if (progress.EpisodesCreated > 0) {
            created.push(progress.EpisodesCreated + ' episodes');
        }
        if (created.length > 0) {
            html += '<br/>Created: ' + created.join(', ');
        }

        html += '</span>';
        statusSpan.innerHTML = html;
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
            this.updateFailedItemsDisplay([]);
            return;
        }

        const startTime = new Date(result.StartTime).toLocaleString();
        const status = result.Success ? '<span style="color: green;">Success</span>' : '<span style="color: red;">Failed</span>';

        let html = '<strong>Last Sync:</strong> ' + startTime + ' - ' + status + '<br/><br/>';

        // Movies section
        html += '<strong>Movies</strong><br/>';
        html += '&nbsp;&nbsp;Total: ' + (result.TotalMovies || (result.MoviesCreated + result.MoviesSkipped)) + '<br/>';
        html += '&nbsp;&nbsp;' + result.MoviesCreated + ' added, ' + (result.MoviesDeleted || 0) + ' deleted';
        html += '<br/><br/>';

        // Series section
        html += '<strong>Series</strong><br/>';
        html += '&nbsp;&nbsp;Total: ' + (result.TotalSeries || (result.SeriesCreated + result.SeriesSkipped) || 0) + '<br/>';
        html += '&nbsp;&nbsp;' + (result.SeriesCreated || 0) + ' added, ' + (result.SeriesDeleted || 0) + ' deleted';
        html += '<br/>';

        // Seasons
        html += '&nbsp;&nbsp;Seasons: ' + (result.TotalSeasons || (result.SeasonsCreated + result.SeasonsSkipped) || 0) + ' total';
        html += ', ' + (result.SeasonsCreated || 0) + ' added, ' + (result.SeasonsDeleted || 0) + ' deleted';
        html += '<br/>';

        // Episodes
        html += '&nbsp;&nbsp;Episodes: ' + (result.TotalEpisodes || (result.EpisodesCreated + result.EpisodesSkipped)) + ' total';
        html += ', ' + result.EpisodesCreated + ' added, ' + (result.EpisodesDeleted || 0) + ' deleted';

        // Errors
        if (result.Errors > 0) {
            html += '<br/><br/><span style="color: orange;"><strong>Errors:</strong> ' + result.Errors + '</span>';
        }
        if (result.Error) {
            html += '<br/><span style="color: red;"><strong>Error:</strong> ' + result.Error + '</span>';
        }

        infoDiv.innerHTML = html;

        // Update failed items display
        this.updateFailedItemsDisplay(result.FailedItems || []);
    },

    updateFailedItemsDisplay: function (failedItems) {
        const btnRetry = document.getElementById('btnRetryFailed');
        const failedCount = document.getElementById('failedCount');
        const failedList = document.getElementById('failedItemsList');
        const failedContent = document.getElementById('failedItemsContent');

        if (failedItems.length > 0) {
            btnRetry.style.display = 'inline-block';
            failedCount.textContent = failedItems.length;
            failedList.style.display = 'block';

            let html = '<ul style="margin: 5px 0; padding-left: 20px;">';
            failedItems.forEach(function (item) {
                html += '<li><span style="color: orange;">' + XtreamLibraryConfig.escapeHtml(item.ItemType) + ':</span> ';
                html += XtreamLibraryConfig.escapeHtml(item.Name);
                if (item.ErrorMessage) {
                    html += ' <span style="color: #888;">(' + XtreamLibraryConfig.escapeHtml(item.ErrorMessage) + ')</span>';
                }
                html += '</li>';
            });
            html += '</ul>';
            failedContent.innerHTML = html;
        } else {
            btnRetry.style.display = 'none';
            failedList.style.display = 'none';
            failedContent.innerHTML = '';
        }
    },

    retryFailed: function () {
        const statusSpan = document.getElementById('syncStatus');
        const self = this;

        statusSpan.innerHTML = '<span style="color: orange;">Retrying failed items...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/RetryFailed'), {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            if (data.Success) {
                statusSpan.innerHTML = '<span style="color: green;">Retry completed!</span>';
                // Reload status to get updated results
                self.loadSyncStatus();
            } else {
                statusSpan.innerHTML = '<span style="color: red;">Retry failed: ' + (data.Error || 'Unknown error') + '</span>';
            }
        }).catch(function (error) {
            console.error('Retry error:', error);
            statusSpan.innerHTML = '<span style="color: red;">Retry failed: ' + (error.message || 'Check console for details') + '</span>';
        });
    },

    loadVodCategories: function () {
        const statusSpan = document.getElementById('vodCategoryLoadStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Loading...</span>';

        const self = this;

        fetch(ApiClient.getUrl('XtreamLibrary/Categories/Vod'), {
            method: 'GET',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (r) {
            return r.ok ? r.json() : Promise.reject(r);
        }).then(function (categories) {
            self.vodCategories = categories || [];
            self.renderCategoryList('vod', self.vodCategories, self.selectedVodCategoryIds);
            document.getElementById('vodCategoriesSection').style.display = 'block';
            statusSpan.innerHTML = '<span style="color: green;">Loaded ' + self.vodCategories.length + ' categories</span>';
        }).catch(function (error) {
            console.error('Failed to load VOD categories:', error);
            statusSpan.innerHTML = '<span style="color: red;">Failed to load. Check credentials.</span>';
        });
    },

    loadSeriesCategories: function () {
        const statusSpan = document.getElementById('seriesCategoryLoadStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Loading...</span>';

        const self = this;

        fetch(ApiClient.getUrl('XtreamLibrary/Categories/Series'), {
            method: 'GET',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (r) {
            return r.ok ? r.json() : Promise.reject(r);
        }).then(function (categories) {
            self.seriesCategories = categories || [];
            self.renderCategoryList('series', self.seriesCategories, self.selectedSeriesCategoryIds);
            document.getElementById('seriesCategoriesSection').style.display = 'block';
            statusSpan.innerHTML = '<span style="color: green;">Loaded ' + self.seriesCategories.length + ' categories</span>';
        }).catch(function (error) {
            console.error('Failed to load Series categories:', error);
            statusSpan.innerHTML = '<span style="color: red;">Failed to load. Check credentials.</span>';
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
    },

    clearMetadataCache: function () {
        const statusSpan = document.getElementById('metadataCacheStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Clearing...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/ClearMetadataCache'), {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            if (data.Success) {
                statusSpan.innerHTML = '<span style="color: green;">' + data.Message + '</span>';
            } else {
                statusSpan.innerHTML = '<span style="color: red;">Failed to clear cache.</span>';
            }
        }).catch(function (error) {
            console.error('ClearMetadataCache error:', error);
            statusSpan.innerHTML = '<span style="color: red;">Failed: ' + (error.message || 'Check console for details') + '</span>';
        });
    },

    cleanLibraries: function () {
        if (!confirm('Are you sure you want to delete ALL Movies and Series content?\n\nThis action cannot be undone.')) {
            return;
        }

        const statusSpan = document.getElementById('cleanLibrariesStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Deleting...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/CleanLibraries'), {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            if (data.Success) {
                statusSpan.innerHTML = '<span style="color: green;">' + data.Message + '</span>';
            } else {
                statusSpan.innerHTML = '<span style="color: red;">' + (data.Message || 'Failed to clean libraries.') + '</span>';
            }
        }).catch(function (error) {
            console.error('CleanLibraries error:', error);
            statusSpan.innerHTML = '<span style="color: red;">Failed: ' + (error.message || 'Check console for details') + '</span>';
        });
    }
};

// Initialize when DOM is ready
function initXtreamLibraryConfig() {
    const form = document.getElementById('XtreamLibraryConfigForm');
    const btnTest = document.getElementById('btnTestConnection');
    const btnSync = document.getElementById('btnManualSync');
    const btnLoadVodCategories = document.getElementById('btnLoadVodCategories');
    const btnLoadSeriesCategories = document.getElementById('btnLoadSeriesCategories');

    // Tab switching
    document.querySelectorAll('.xtream-tab').forEach(function (tab) {
        tab.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.switchTab(tab.getAttribute('data-tab'));
        });
    });

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
            if (XtreamLibraryConfig.isSyncing) {
                XtreamLibraryConfig.cancelSync();
            } else {
                XtreamLibraryConfig.runSync();
            }
        });
    }

    const btnRetryFailed = document.getElementById('btnRetryFailed');
    if (btnRetryFailed) {
        btnRetryFailed.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.retryFailed();
        });
    }

    if (btnLoadVodCategories) {
        btnLoadVodCategories.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.loadVodCategories();
        });
    }

    if (btnLoadSeriesCategories) {
        btnLoadSeriesCategories.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.loadSeriesCategories();
        });
    }

    var btnClearMetadataCache = document.getElementById('btnClearMetadataCache');
    if (btnClearMetadataCache) {
        btnClearMetadataCache.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.clearMetadataCache();
        });
    }

    var btnCleanLibraries = document.getElementById('btnCleanLibraries');
    if (btnCleanLibraries) {
        btnCleanLibraries.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.cleanLibraries();
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
