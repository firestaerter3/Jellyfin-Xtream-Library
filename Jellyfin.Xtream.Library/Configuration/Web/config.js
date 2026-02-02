const XtreamLibraryConfig = {
    pluginUniqueId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',

    // Cache for loaded categories
    vodCategories: [],
    seriesCategories: [],
    liveCategories: [],
    selectedVodCategoryIds: [],
    selectedSeriesCategoryIds: [],
    selectedLiveCategoryIds: [],

    // Folder definitions for multi-folder mode
    // Each entry: { name: 'FolderName', categoryIds: [1, 2, 3] }
    vodFolderDefinitions: [],
    seriesFolderDefinitions: [],

    // Track last clicked checkbox per category type for shift+click range selection
    lastClickedIndex: { vod: null, series: null, live: null },

    // Tab switching
    switchTab: function (tabName) {
        document.querySelectorAll('.xtream-tab').forEach(function (tab) {
            tab.classList.remove('active');
            if (tab.getAttribute('data-tab') === tabName) {
                tab.classList.add('active');
            }
        });
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
            self.selectedVodCategoryIds = config.SelectedVodCategoryIds || [];
            self.selectedSeriesCategoryIds = config.SelectedSeriesCategoryIds || [];

            // Folder ID overrides
            document.getElementById('txtTmdbFolderIdOverrides').value = config.TmdbFolderIdOverrides || '';
            document.getElementById('txtTvdbFolderIdOverrides').value = config.TvdbFolderIdOverrides || '';

            // Folder mode
            document.getElementById('selMovieFolderMode').value = config.MovieFolderMode || 'Single';
            document.getElementById('selSeriesFolderMode').value = config.SeriesFolderMode || 'Single';

            // Parse folder mappings into definitions
            self.vodFolderDefinitions = self.parseFolderMappings(config.MovieFolderMappings);
            self.seriesFolderDefinitions = self.parseFolderMappings(config.SeriesFolderMappings);

            // Metadata lookup
            document.getElementById('chkEnableMetadataLookup').checked = config.EnableMetadataLookup || false;
            document.getElementById('txtMetadataParallelism').value = config.MetadataParallelism || 3;
            document.getElementById('txtCategoryBatchSize').value = config.CategoryBatchSize || 10;

            // Artwork download for unmatched
            document.getElementById('chkDownloadArtworkForUnmatched').checked = config.DownloadArtworkForUnmatched !== false;

            // Proactive media info
            document.getElementById('chkEnableProactiveMediaInfo').checked = config.EnableProactiveMediaInfo || false;

            // Schedule settings
            document.getElementById('selSyncScheduleType').value = config.SyncScheduleType || 'Interval';
            document.getElementById('selSyncDailyHour').value = config.SyncDailyHour || 3;
            document.getElementById('selSyncDailyMinute').value = config.SyncDailyMinute || 0;
            self.updateScheduleVisibility();

            // Update folder mode visibility
            self.updateFolderModeVisibility('vod');
            self.updateFolderModeVisibility('series');

            // Live TV settings
            document.getElementById('chkEnableLiveTv').checked = config.EnableLiveTv || false;
            document.getElementById('chkEnableEpg').checked = config.EnableEpg !== false;
            document.getElementById('selLiveTvOutputFormat').value = config.LiveTvOutputFormat || 'm3u8';
            document.getElementById('chkIncludeAdultChannels').checked = config.IncludeAdultChannels || false;
            document.getElementById('txtM3UCacheMinutes').value = config.M3UCacheMinutes || 15;
            document.getElementById('txtEpgCacheMinutes').value = config.EpgCacheMinutes || 30;
            document.getElementById('txtEpgDaysToFetch').value = config.EpgDaysToFetch || 2;
            document.getElementById('txtEpgParallelism').value = config.EpgParallelism || 5;
            self.selectedLiveCategoryIds = config.SelectedLiveCategoryIds || [];

            // Title cleaning
            document.getElementById('chkEnableChannelNameCleaning').checked = config.EnableChannelNameCleaning !== false;
            document.getElementById('txtChannelRemoveTerms').value = config.ChannelRemoveTerms || '';

            // Channel overrides
            document.getElementById('txtChannelOverrides').value = config.ChannelOverrides || '';

            // Catch-up
            document.getElementById('chkEnableCatchup').checked = config.EnableCatchup || false;
            document.getElementById('txtCatchupDays').value = config.CatchupDays || 7;

            // Update Live TV URLs
            self.updateLiveTvUrls();

            Dashboard.hideLoadingMsg();

            // Auto-load categories if credentials are configured
            if (config.BaseUrl && config.Username) {
                self.loadVodCategories();
                self.loadSeriesCategories();
                self.loadLiveCategories();
            }
        });

        this.loadSyncStatus();
    },

    saveConfig: function () {
        Dashboard.showLoadingMsg();
        const self = this;

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

            // Folder mode
            config.MovieFolderMode = document.getElementById('selMovieFolderMode').value;
            config.SeriesFolderMode = document.getElementById('selSeriesFolderMode').value;

            // Get selected category IDs based on folder mode
            if (config.MovieFolderMode === 'Single') {
                config.SelectedVodCategoryIds = self.getSelectedCategoryIds('vod');
                config.MovieFolderMappings = '';
            } else {
                // In multi-folder mode, collect all categories from folder definitions
                self.updateFolderDefinitionsFromUI('vod');
                config.SelectedVodCategoryIds = self.getAllCategoryIdsFromFolders('vod');
                config.MovieFolderMappings = self.buildFolderMappings(self.vodFolderDefinitions);
            }

            if (config.SeriesFolderMode === 'Single') {
                config.SelectedSeriesCategoryIds = self.getSelectedCategoryIds('series');
                config.SeriesFolderMappings = '';
            } else {
                self.updateFolderDefinitionsFromUI('series');
                config.SelectedSeriesCategoryIds = self.getAllCategoryIdsFromFolders('series');
                config.SeriesFolderMappings = self.buildFolderMappings(self.seriesFolderDefinitions);
            }

            // Folder ID overrides
            config.TmdbFolderIdOverrides = document.getElementById('txtTmdbFolderIdOverrides').value;
            config.TvdbFolderIdOverrides = document.getElementById('txtTvdbFolderIdOverrides').value;

            // Metadata lookup
            config.EnableMetadataLookup = document.getElementById('chkEnableMetadataLookup').checked;
            config.MetadataParallelism = parseInt(document.getElementById('txtMetadataParallelism').value) || 3;
            config.CategoryBatchSize = parseInt(document.getElementById('txtCategoryBatchSize').value) || 10;

            // Artwork download for unmatched
            config.DownloadArtworkForUnmatched = document.getElementById('chkDownloadArtworkForUnmatched').checked;

            // Proactive media info
            config.EnableProactiveMediaInfo = document.getElementById('chkEnableProactiveMediaInfo').checked;

            // Schedule settings
            config.SyncScheduleType = document.getElementById('selSyncScheduleType').value;
            config.SyncDailyHour = parseInt(document.getElementById('selSyncDailyHour').value) || 3;
            config.SyncDailyMinute = parseInt(document.getElementById('selSyncDailyMinute').value) || 0;

            // Live TV settings
            config.EnableLiveTv = document.getElementById('chkEnableLiveTv').checked;
            config.EnableEpg = document.getElementById('chkEnableEpg').checked;
            config.LiveTvOutputFormat = document.getElementById('selLiveTvOutputFormat').value;
            config.IncludeAdultChannels = document.getElementById('chkIncludeAdultChannels').checked;
            config.M3UCacheMinutes = parseInt(document.getElementById('txtM3UCacheMinutes').value) || 15;
            config.EpgCacheMinutes = parseInt(document.getElementById('txtEpgCacheMinutes').value) || 30;
            config.EpgDaysToFetch = parseInt(document.getElementById('txtEpgDaysToFetch').value) || 2;
            config.EpgParallelism = parseInt(document.getElementById('txtEpgParallelism').value) || 5;
            config.SelectedLiveCategoryIds = self.getSelectedCategoryIds('live');

            // Title cleaning
            config.EnableChannelNameCleaning = document.getElementById('chkEnableChannelNameCleaning').checked;
            config.ChannelRemoveTerms = document.getElementById('txtChannelRemoveTerms').value;

            // Channel overrides
            config.ChannelOverrides = document.getElementById('txtChannelOverrides').value;

            // Catch-up
            config.EnableCatchup = document.getElementById('chkEnableCatchup').checked;
            config.CatchupDays = parseInt(document.getElementById('txtCatchupDays').value) || 7;

            ApiClient.updatePluginConfiguration(self.pluginUniqueId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    },

    // Parse folder mappings string into array of folder definitions
    parseFolderMappings: function (mappingsStr) {
        var result = [];
        if (!mappingsStr) return result;

        var lines = mappingsStr.split('\n');
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            if (!line) continue;

            var eqIdx = line.indexOf('=');
            if (eqIdx <= 0) continue;

            var name = line.substring(0, eqIdx).trim();
            var idsStr = line.substring(eqIdx + 1).trim();
            var ids = [];

            var idParts = idsStr.split(',');
            for (var j = 0; j < idParts.length; j++) {
                var id = parseInt(idParts[j].trim());
                if (!isNaN(id)) {
                    ids.push(id);
                }
            }

            if (name && ids.length > 0) {
                result.push({ name: name, categoryIds: ids });
            }
        }
        return result;
    },

    // Build folder mappings string from folder definitions
    buildFolderMappings: function (definitions) {
        var lines = [];
        for (var i = 0; i < definitions.length; i++) {
            var def = definitions[i];
            if (def.name && def.categoryIds.length > 0) {
                lines.push(def.name + '=' + def.categoryIds.join(','));
            }
        }
        return lines.join('\n');
    },

    // Get all category IDs from folder definitions
    getAllCategoryIdsFromFolders: function (type) {
        var definitions = type === 'vod' ? this.vodFolderDefinitions : this.seriesFolderDefinitions;
        var allIds = [];
        for (var i = 0; i < definitions.length; i++) {
            for (var j = 0; j < definitions[i].categoryIds.length; j++) {
                var id = definitions[i].categoryIds[j];
                if (allIds.indexOf(id) === -1) {
                    allIds.push(id);
                }
            }
        }
        return allIds;
    },

    // Update folder definitions from UI checkboxes
    updateFolderDefinitionsFromUI: function (type) {
        var definitions = type === 'vod' ? this.vodFolderDefinitions : this.seriesFolderDefinitions;
        var listId = type === 'vod' ? 'vodFolderList' : 'seriesFolderList';
        var container = document.getElementById(listId);
        var folderItems = container.querySelectorAll('.folder-item');

        definitions.length = 0; // Clear array

        folderItems.forEach(function (item, index) {
            var nameInput = item.querySelector('.folder-name-input');
            var checkboxes = item.querySelectorAll('input[type="checkbox"]:checked');
            var categoryIds = [];

            checkboxes.forEach(function (cb) {
                categoryIds.push(parseInt(cb.getAttribute('data-category-id')));
            });

            if (nameInput.value.trim()) {
                definitions.push({
                    name: nameInput.value.trim(),
                    categoryIds: categoryIds
                });
            }
        });
    },

    // Update visibility based on folder mode
    updateFolderModeVisibility: function (type) {
        var modeSelect = document.getElementById(type === 'vod' ? 'selMovieFolderMode' : 'selSeriesFolderMode');
        var singleSection = document.getElementById(type === 'vod' ? 'vodSingleFolderSection' : 'seriesSingleFolderSection');
        var multiSection = document.getElementById(type === 'vod' ? 'vodMultiFolderSection' : 'seriesMultiFolderSection');
        var descElem = document.getElementById(type === 'vod' ? 'vodCategoryDescription' : 'seriesCategoryDescription');

        var mode = modeSelect.value;

        if (mode === 'Multiple') {
            singleSection.style.display = 'none';
            multiSection.style.display = 'block';
            descElem.textContent = 'Create folders and assign categories to each. Categories can be in multiple folders.';
            this.renderFolderList(type);
        } else {
            singleSection.style.display = 'block';
            multiSection.style.display = 'none';
            descElem.textContent = 'Select specific categories to sync. Leave all unchecked to sync all categories.';
        }
    },

    // Add a new folder definition
    addFolder: function (type) {
        var definitions = type === 'vod' ? this.vodFolderDefinitions : this.seriesFolderDefinitions;
        definitions.push({ name: '', categoryIds: [] });
        this.renderFolderList(type);
    },

    // Remove a folder definition
    removeFolder: function (type, index) {
        var definitions = type === 'vod' ? this.vodFolderDefinitions : this.seriesFolderDefinitions;
        definitions.splice(index, 1);
        this.renderFolderList(type);
    },

    // Render the folder list for multi-folder mode
    renderFolderList: function (type) {
        var definitions = type === 'vod' ? this.vodFolderDefinitions : this.seriesFolderDefinitions;
        var categories = type === 'vod' ? this.vodCategories : this.seriesCategories;
        var listId = type === 'vod' ? 'vodFolderList' : 'seriesFolderList';
        var container = document.getElementById(listId);

        if (categories.length === 0) {
            container.innerHTML = '<div class="fieldDescription">Load categories first to configure folders.</div>';
            return;
        }

        var html = '';
        var self = this;

        definitions.forEach(function (folder, folderIndex) {
            html += '<div class="folder-item" data-folder-index="' + folderIndex + '">';
            html += '<div class="folder-item-header">';
            html += '<input type="text" class="folder-name-input" placeholder="Folder name (e.g., Kids)" value="' + self.escapeHtml(folder.name) + '" style="padding: 8px; border-radius: 4px; border: 1px solid rgba(255,255,255,0.2); background: rgba(0,0,0,0.3); color: #fff;"/>';
            html += '<button type="button" class="raised" onclick="XtreamLibraryConfig.removeFolder(\'' + type + '\', ' + folderIndex + ')" style="background: #c0392b; padding: 8px 12px; border: none; border-radius: 4px; color: #fff; cursor: pointer;">Remove</button>';
            html += '</div>';
            html += '<div class="category-list">';

            categories.forEach(function (category, catIndex) {
                var isChecked = folder.categoryIds.indexOf(category.CategoryId) !== -1 ? 'checked' : '';
                var checkboxId = type + '_folder' + folderIndex + '_cat' + category.CategoryId;
                html += '<div class="checkboxContainer">';
                html += '<label class="emby-checkbox-label">';
                html += '<input type="checkbox" id="' + checkboxId + '" ';
                html += 'data-category-id="' + category.CategoryId + '" ';
                html += 'data-folder-index="' + folderIndex + '" ';
                html += 'data-category-index="' + catIndex + '" ' + isChecked + '/>';
                html += '<span>' + self.escapeHtml(category.CategoryName) + ' <small style="opacity:0.5;">(ID: ' + category.CategoryId + ')</small></span>';
                html += '</label>';
                html += '</div>';
            });

            html += '</div>';
            html += '</div>';
        });

        if (definitions.length === 0) {
            html += '<div class="fieldDescription">No folders defined. Click "Add Folder" to create one.</div>';
        }

        container.innerHTML = html;

        // Add shift+click support for each folder's category list
        definitions.forEach(function (folder, folderIndex) {
            var folderItem = container.querySelector('.folder-item[data-folder-index="' + folderIndex + '"]');
            if (folderItem) {
                var checkboxes = folderItem.querySelectorAll('input[type="checkbox"]');
                var lastClickedIdx = null;

                checkboxes.forEach(function (checkbox) {
                    checkbox.addEventListener('click', function (e) {
                        var currentIdx = parseInt(checkbox.getAttribute('data-category-index'));
                        if (e.shiftKey && lastClickedIdx !== null && lastClickedIdx !== currentIdx) {
                            var start = Math.min(lastClickedIdx, currentIdx);
                            var end = Math.max(lastClickedIdx, currentIdx);
                            var newState = checkbox.checked;
                            for (var i = start; i <= end; i++) {
                                checkboxes[i].checked = newState;
                            }
                        }
                        lastClickedIdx = currentIdx;
                    });
                });
            }
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

    progressInterval: null,
    isSyncing: false,

    runSync: function () {
        const statusSpan = document.getElementById('syncStatus');
        const syncBtn = document.getElementById('btnManualSync');
        const self = this;

        self.isSyncing = true;
        syncBtn.querySelector('span').textContent = 'Cancel Sync';
        syncBtn.style.background = '#c0392b';

        // Start sync in background
        fetch(ApiClient.getUrl('XtreamLibrary/Sync'), {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            if (data.Success) {
                // Sync started successfully, begin polling for progress and completion
                statusSpan.innerHTML = '<span style="color: orange;">Sync started...</span>';
                self.startProgressPolling();
                self.pollForCompletion();
            } else if (data.Message && data.Message.includes('already in progress')) {
                // Sync already running, just start polling
                statusSpan.innerHTML = '<span style="color: orange;">Sync already in progress...</span>';
                self.startProgressPolling();
                self.pollForCompletion();
            } else {
                self.resetSyncButton();
                statusSpan.innerHTML = '<span style="color: red;">Failed to start sync: ' + (data.Message || 'Unknown error') + '</span>';
            }
        }).catch(function (error) {
            self.resetSyncButton();
            console.error('Sync error:', error);
            statusSpan.innerHTML = '<span style="color: red;">Sync failed: ' + (error.message || 'Check console for details') + '</span>';
        });
    },

    pollForCompletion: function () {
        const self = this;
        const statusSpan = document.getElementById('syncStatus');

        self.completionInterval = setInterval(function () {
            fetch(ApiClient.getUrl('XtreamLibrary/Progress'), {
                method: 'GET',
                headers: {
                    'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
                }
            }).then(function (r) {
                return r.ok ? r.json() : null;
            }).then(function (progress) {
                if (!progress || !progress.IsRunning) {
                    // Sync finished, stop polling and fetch final status
                    self.stopCompletionPolling();
                    self.stopProgressPolling();
                    self.resetSyncButton();
                    self.loadSyncStatus();
                    // Check if it was success or failure
                    fetch(ApiClient.getUrl('XtreamLibrary/Status'), {
                        method: 'GET',
                        headers: {
                            'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
                        }
                    }).then(function (r) {
                        return r.ok ? r.json() : null;
                    }).then(function (result) {
                        if (result) {
                            if (result.Success) {
                                statusSpan.innerHTML = '<span style="color: green;">Sync completed!</span>';
                            } else if (result.Error && result.Error.toLowerCase().includes('cancel')) {
                                statusSpan.innerHTML = '<span style="color: orange;">Sync was cancelled.</span>';
                            } else {
                                statusSpan.innerHTML = '<span style="color: red;">Sync failed: ' + (result.Error || 'Unknown error') + '</span>';
                            }
                        }
                    });
                }
            }).catch(function () {});
        }, 1000);
    },

    stopCompletionPolling: function () {
        if (this.completionInterval) {
            clearInterval(this.completionInterval);
            this.completionInterval = null;
        }
    },

    cancelSync: function () {
        const statusSpan = document.getElementById('syncStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Cancelling sync...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/Cancel'), {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (response) {
            return response.json();
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
        statusSpan.innerHTML = '<span style="color: orange;">Starting sync...</span>';

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
            }).catch(function () {});
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
        html += progress.Phase;
        if (progress.CurrentItem) {
            html += ': ' + this.escapeHtml(progress.CurrentItem);
        }
        if (progress.TotalCategories > 0) {
            html += '<br/>Categories: ' + progress.CategoriesProcessed + '/' + progress.TotalCategories;
        }
        if (progress.TotalItems > 0) {
            html += ' | Items: ' + progress.ItemsProcessed + '/' + progress.TotalItems;
        }
        const created = [];
        if (progress.MoviesCreated > 0) created.push(progress.MoviesCreated + ' movies');
        if (progress.EpisodesCreated > 0) created.push(progress.EpisodesCreated + ' episodes');
        if (created.length > 0) {
            html += '<br/>Created: ' + created.join(', ');
        }
        html += '</span>';
        statusSpan.innerHTML = html;
    },

    loadSyncStatus: function () {
        const self = this;
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
                self.displaySyncResult(data);
            }
        }).catch(function () {});
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
        html += '<strong>Movies</strong><br/>';
        html += '&nbsp;&nbsp;Total: ' + (result.TotalMovies || (result.MoviesCreated + result.MoviesSkipped)) + '<br/>';
        html += '&nbsp;&nbsp;' + result.MoviesCreated + ' added, ' + (result.MoviesDeleted || 0) + ' deleted<br/><br/>';
        html += '<strong>Series</strong><br/>';
        html += '&nbsp;&nbsp;Total: ' + (result.TotalSeries || (result.SeriesCreated + result.SeriesSkipped) || 0) + '<br/>';
        html += '&nbsp;&nbsp;' + (result.SeriesCreated || 0) + ' added, ' + (result.SeriesDeleted || 0) + ' deleted<br/>';
        html += '&nbsp;&nbsp;Seasons: ' + (result.TotalSeasons || (result.SeasonsCreated + result.SeasonsSkipped) || 0) + ' total';
        html += ', ' + (result.SeasonsCreated || 0) + ' added, ' + (result.SeasonsDeleted || 0) + ' deleted<br/>';
        html += '&nbsp;&nbsp;Episodes: ' + (result.TotalEpisodes || (result.EpisodesCreated + result.EpisodesSkipped)) + ' total';
        html += ', ' + result.EpisodesCreated + ' added, ' + (result.EpisodesDeleted || 0) + ' deleted';

        if (result.Errors > 0) {
            html += '<br/><br/><span style="color: orange;"><strong>Errors:</strong> ' + result.Errors + '</span>';
        }
        if (result.Error) {
            html += '<br/><span style="color: red;"><strong>Error:</strong> ' + result.Error + '</span>';
        }

        infoDiv.innerHTML = html;
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
            var mode = document.getElementById('selMovieFolderMode').value;
            if (mode === 'Single') {
                self.renderCategoryList('vod', self.vodCategories, self.selectedVodCategoryIds);
                document.getElementById('vodSingleFolderSection').style.display = 'block';
            } else {
                self.renderFolderList('vod');
                document.getElementById('vodMultiFolderSection').style.display = 'block';
            }
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
            var mode = document.getElementById('selSeriesFolderMode').value;
            if (mode === 'Single') {
                self.renderCategoryList('series', self.seriesCategories, self.selectedSeriesCategoryIds);
                document.getElementById('seriesSingleFolderSection').style.display = 'block';
            } else {
                self.renderFolderList('series');
                document.getElementById('seriesMultiFolderSection').style.display = 'block';
            }
            statusSpan.innerHTML = '<span style="color: green;">Loaded ' + self.seriesCategories.length + ' categories</span>';
        }).catch(function (error) {
            console.error('Failed to load Series categories:', error);
            statusSpan.innerHTML = '<span style="color: red;">Failed to load. Check credentials.</span>';
        });
    },

    renderCategoryList: function (type, categories, selectedIds) {
        var listId;
        if (type === 'vod') {
            listId = 'vodCategoryList';
        } else if (type === 'series') {
            listId = 'seriesCategoryList';
        } else {
            listId = 'liveCategoryList';
        }
        const container = document.getElementById(listId);

        if (!categories || categories.length === 0) {
            container.innerHTML = '<div class="fieldDescription">No categories found.</div>';
            return;
        }

        let html = '';
        const self = this;
        categories.forEach(function (category, index) {
            const isChecked = selectedIds.indexOf(category.CategoryId) !== -1 ? 'checked' : '';
            const checkboxId = type + 'Cat_' + category.CategoryId;
            html += '<div class="checkboxContainer">';
            html += '<label class="emby-checkbox-label">';
            html += '<input is="emby-checkbox" type="checkbox" id="' + checkboxId + '" ';
            html += 'data-category-id="' + category.CategoryId + '" data-category-type="' + type + '" ';
            html += 'data-index="' + index + '" ' + isChecked + '/>';
            html += '<span>' + self.escapeHtml(category.CategoryName) + ' <small style="opacity:0.5;">(ID: ' + category.CategoryId + ')</small></span>';
            html += '</label>';
            html += '</div>';
        });

        container.innerHTML = html;

        // Add shift+click range selection support
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

    updateScheduleVisibility: function () {
        const scheduleType = document.getElementById('selSyncScheduleType').value;
        const intervalSettings = document.getElementById('intervalSettings');
        const dailySettings = document.getElementById('dailySettings');

        if (scheduleType === 'Daily') {
            intervalSettings.style.display = 'none';
            dailySettings.style.display = 'block';
        } else {
            intervalSettings.style.display = 'block';
            dailySettings.style.display = 'none';
        }
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
    },

    // Live TV functions
    loadLiveCategories: function () {
        const statusSpan = document.getElementById('liveCategoryLoadStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Loading...</span>';
        const self = this;

        fetch(ApiClient.getUrl('XtreamLibrary/Categories/Live'), {
            method: 'GET',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (r) {
            return r.ok ? r.json() : Promise.reject(r);
        }).then(function (categories) {
            self.liveCategories = categories || [];
            self.renderCategoryList('live', self.liveCategories, self.selectedLiveCategoryIds);
            document.getElementById('liveSingleFolderSection').style.display = 'block';
            statusSpan.innerHTML = '<span style="color: green;">Loaded ' + self.liveCategories.length + ' categories</span>';
        }).catch(function (error) {
            console.error('Failed to load Live TV categories:', error);
            statusSpan.innerHTML = '<span style="color: red;">Failed to load. Check credentials.</span>';
        });
    },

    updateLiveTvUrls: function () {
        var baseUrl = window.location.origin;
        document.getElementById('txtM3UUrl').value = baseUrl + '/XtreamLibrary/LiveTv.m3u';
        document.getElementById('txtEpgUrl').value = baseUrl + '/XtreamLibrary/Epg.xml';
        document.getElementById('txtCatchupUrl').value = baseUrl + '/XtreamLibrary/Catchup.m3u';
    },

    copyToClipboard: function (elementId) {
        var input = document.getElementById(elementId);
        input.select();
        input.setSelectionRange(0, 99999);
        navigator.clipboard.writeText(input.value).then(function () {
            // Brief visual feedback
            var originalBg = input.style.background;
            input.style.background = 'rgba(0, 200, 0, 0.3)';
            setTimeout(function () {
                input.style.background = originalBg;
            }, 500);
        }).catch(function (err) {
            console.error('Failed to copy:', err);
        });
    },

    refreshLiveTvCache: function () {
        const statusSpan = document.getElementById('liveTvCacheStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Refreshing...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/LiveTv/RefreshCache'), {
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
                statusSpan.innerHTML = '<span style="color: red;">Failed to refresh cache.</span>';
            }
        }).catch(function (error) {
            console.error('RefreshLiveTvCache error:', error);
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

    var selSyncScheduleType = document.getElementById('selSyncScheduleType');
    if (selSyncScheduleType) {
        selSyncScheduleType.addEventListener('change', function () {
            XtreamLibraryConfig.updateScheduleVisibility();
        });
    }

    // Folder mode change handlers
    var selMovieFolderMode = document.getElementById('selMovieFolderMode');
    if (selMovieFolderMode) {
        selMovieFolderMode.addEventListener('change', function () {
            XtreamLibraryConfig.updateFolderModeVisibility('vod');
        });
    }

    var selSeriesFolderMode = document.getElementById('selSeriesFolderMode');
    if (selSeriesFolderMode) {
        selSeriesFolderMode.addEventListener('change', function () {
            XtreamLibraryConfig.updateFolderModeVisibility('series');
        });
    }

    // Live TV event handlers
    var btnLoadLiveCategories = document.getElementById('btnLoadLiveCategories');
    if (btnLoadLiveCategories) {
        btnLoadLiveCategories.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.loadLiveCategories();
        });
    }

    var btnRefreshLiveTvCache = document.getElementById('btnRefreshLiveTvCache');
    if (btnRefreshLiveTvCache) {
        btnRefreshLiveTvCache.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.refreshLiveTvCache();
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
