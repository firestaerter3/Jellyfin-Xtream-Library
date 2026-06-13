const XtreamLibraryConfig = {
    pluginUniqueId: '63ba5fcd-c8ce-421a-83e8-ba0b11030d53',

    // Multi-provider state
    providers: [],
    activeProviderIndex: 0,

    // Cache for loaded categories
    vodCategories: [],
    seriesCategories: [],
    liveCategories: [],
    selectedVodCategoryIds: [],
    selectedSeriesCategoryIds: [],
    selectedLiveCategoryIds: [],

    // Per-channel Live TV exclusions (stream IDs that are unchecked under their category)
    excludedLiveStreamIds: [],
    // Session cache of channels per category (not persisted)
    liveChannelsByCategory: {},
    // Track which Live TV categories are currently expanded in the UI
    expandedLiveCategories: {},

    // Per-item VOD/Series exclusions (item IDs unchecked under their category) — per active provider
    excludedVodStreamIds: [],
    excludedSeriesIds: [],
    // Session cache of items per category, keyed by type then category id (not persisted)
    contentItemsByCategory: { vod: {}, series: {} },
    // Track which VOD/Series categories are currently expanded in the UI, keyed by type
    expandedContentCategories: { vod: {}, series: {} },

    // Folder definitions for multi-folder mode
    // Each entry: { name: 'FolderName', categoryIds: [1, 2, 3] }
    vodFolderDefinitions: [],
    seriesFolderDefinitions: [],

    // Track last clicked checkbox per category type for shift+click range selection
    lastClickedIndex: { vod: null, series: null, live: null },

    // Dashboard polling
    dashboardProgressInterval: null,

    // Creates a new provider config object with sensible defaults
    makeDefaultProvider: function (index) {
        return {
            Name: 'Provider ' + (index + 1),
            IsEnabled: true,
            BaseUrl: '',
            Username: '',
            Password: '',
            UserAgent: '',
            LibraryPath: index === 0 ? '/config/xtream-library' : '/config/xtream-library-' + (index + 1),
            SyncMovies: true,
            SyncSeries: true,
            SelectedVodCategoryIds: [],
            SelectedSeriesCategoryIds: [],
            ExcludedVodStreamIds: [],
            ExcludedSeriesIds: [],
            MovieFolderMode: 'Single',
            SeriesFolderMode: 'Single',
            MovieFolderMappings: '',
            SeriesFolderMappings: '',
            TmdbFolderIdOverrides: '',
            TvdbFolderIdOverrides: '',
            CustomTitleRemoveTerms: '',
            RegexRemovalPatterns: '',
            DownloadArtworkForUnmatched: true,
            FallbackToYearlessLookup: false,
            EnableProactiveMediaInfo: false,
            EnableDispatcharrMode: false,
            DispatcharrApiUser: '',
            DispatcharrApiPass: '',
            EnableIncrementalSync: true,
            FullSyncIntervalDays: 7,
            FullSyncChangeThreshold: 0.5,
            CleanupOrphans: true,
            OrphanSafetyThreshold: 0.5,
            SmartSkipExisting: true,
            SyncParallelism: 10,
            CategoryBatchSize: 25,
            RequestDelayMs: 50,
            MaxRetries: 3,
            RetryDelayMs: 1000,
        };
    },

    // Renders the provider selector <select> element
    renderProviderSelector: function () {
        var sel = document.getElementById('selActiveProvider');
        if (!sel) return;
        sel.innerHTML = '';
        for (var i = 0; i < this.providers.length; i++) {
            var opt = document.createElement('option');
            opt.value = i;
            var label = this.providers[i].Name || ('Provider ' + (i + 1));
            if (!this.providers[i].IsEnabled) label += ' (disabled)';
            opt.textContent = label;
            sel.appendChild(opt);
        }
        sel.value = this.activeProviderIndex;
    },

    // Populates all per-provider UI fields from providers[index]
    loadProviderIntoUI: function (index) {
        var self = this;
        var p = this.providers[index];
        if (!p) return;
        this.activeProviderIndex = index;

        document.getElementById('txtBaseUrl').value = p.BaseUrl || '';
        document.getElementById('txtUsername').value = p.Username || '';
        document.getElementById('txtPassword').value = p.Password || '';
        document.getElementById('txtUserAgent').value = p.UserAgent || '';
        document.getElementById('txtLibraryPath').value = p.LibraryPath || '/config/xtream-library';
        document.getElementById('chkSyncMovies').checked = p.SyncMovies !== false;
        document.getElementById('chkSyncSeries').checked = p.SyncSeries !== false;
        document.getElementById('chkCleanupOrphans').checked = p.CleanupOrphans !== false;

        self.selectedVodCategoryIds = p.SelectedVodCategoryIds || [];
        self.selectedSeriesCategoryIds = p.SelectedSeriesCategoryIds || [];
        self.excludedVodStreamIds = p.ExcludedVodStreamIds || [];
        self.excludedSeriesIds = p.ExcludedSeriesIds || [];
        self.contentItemsByCategory = { vod: {}, series: {} };
        self.expandedContentCategories = { vod: {}, series: {} };

        document.getElementById('txtTmdbFolderIdOverrides').value = p.TmdbFolderIdOverrides || '';
        document.getElementById('txtTvdbFolderIdOverrides').value = p.TvdbFolderIdOverrides || '';

        document.getElementById('selMovieFolderMode').value = p.MovieFolderMode || 'Single';
        document.getElementById('selSeriesFolderMode').value = p.SeriesFolderMode || 'Single';

        self.vodFolderDefinitions = self.parseFolderMappings(p.MovieFolderMappings);
        self.seriesFolderDefinitions = self.parseFolderMappings(p.SeriesFolderMappings);

        document.getElementById('chkFallbackToYearlessLookup').checked = p.FallbackToYearlessLookup === true;

        var txtCustomTerms = document.getElementById('txtCustomTitleRemoveTerms');
        if (txtCustomTerms) txtCustomTerms.value = p.CustomTitleRemoveTerms || '';
        var txtRegexPatterns = document.getElementById('txtRegexRemovalPatterns');
        if (txtRegexPatterns) txtRegexPatterns.value = p.RegexRemovalPatterns || '';

        document.getElementById('txtSyncParallelism').value = p.SyncParallelism || 10;
        document.getElementById('txtCategoryBatchSize').value = p.CategoryBatchSize || 25;
        document.getElementById('txtRequestDelayMs').value = p.RequestDelayMs || 50;
        document.getElementById('txtMaxRetries').value = p.MaxRetries || 3;
        document.getElementById('txtRetryDelayMs').value = p.RetryDelayMs || 1000;

        document.getElementById('chkDownloadArtworkForUnmatched').checked = p.DownloadArtworkForUnmatched !== false;
        document.getElementById('chkEnableIncrementalSync').checked = p.EnableIncrementalSync !== false;
        document.getElementById('txtFullSyncIntervalDays').value = p.FullSyncIntervalDays || 7;
        document.getElementById('txtFullSyncChangeThreshold').value = Math.round((p.FullSyncChangeThreshold || 0.50) * 100);
        document.getElementById('chkEnableProactiveMediaInfo').checked = p.EnableProactiveMediaInfo || false;

        document.getElementById('chkEnableDispatcharrMode').checked = p.EnableDispatcharrMode || false;
        document.getElementById('txtDispatcharrApiUser').value = p.DispatcharrApiUser || '';
        document.getElementById('txtDispatcharrApiPass').value = p.DispatcharrApiPass || '';
        self.updateDispatcharrVisibility();

        self.updateFolderModeVisibility('vod');
        self.updateFolderModeVisibility('series');

        // Clear category lists - they'll be reloaded when user clicks Load
        document.getElementById('vodCategoryList').innerHTML = '';
        document.getElementById('seriesCategoryList').innerHTML = '';
        var vodStatus = document.getElementById('vodCategoryLoadStatus');
        if (vodStatus) vodStatus.innerHTML = '';
        var seriesStatus = document.getElementById('seriesCategoryLoadStatus');
        if (seriesStatus) seriesStatus.innerHTML = '';

        // Auto-load categories if credentials are configured
        if (p.BaseUrl && p.Username) {
            self.loadVodCategories();
            self.loadSeriesCategories();
        }
    },

    // Writes all per-provider UI fields back to providers[activeProviderIndex]
    updateActiveProviderFromUI: function () {
        var p = this.providers[this.activeProviderIndex];
        if (!p) return;

        p.BaseUrl = document.getElementById('txtBaseUrl').value.trim().replace(/\/$/, '');
        p.Username = document.getElementById('txtUsername').value.trim();
        p.Password = document.getElementById('txtPassword').value;
        p.UserAgent = document.getElementById('txtUserAgent').value.trim();
        p.LibraryPath = document.getElementById('txtLibraryPath').value.trim();
        p.SyncMovies = document.getElementById('chkSyncMovies').checked;
        p.SyncSeries = document.getElementById('chkSyncSeries').checked;
        p.CleanupOrphans = document.getElementById('chkCleanupOrphans').checked;

        var movieMode = document.getElementById('selMovieFolderMode').value;
        var seriesMode = document.getElementById('selSeriesFolderMode').value;
        p.MovieFolderMode = movieMode;
        p.SeriesFolderMode = seriesMode;

        if (movieMode === 'Single') {
            p.SelectedVodCategoryIds = this.getSelectedCategoryIds('vod');
            p.MovieFolderMappings = '';
        } else {
            this.updateFolderDefinitionsFromUI('vod');
            p.SelectedVodCategoryIds = this.getAllCategoryIdsFromFolders('vod');
            p.MovieFolderMappings = this.buildFolderMappings(this.vodFolderDefinitions);
        }

        if (seriesMode === 'Single') {
            p.SelectedSeriesCategoryIds = this.getSelectedCategoryIds('series');
            p.SeriesFolderMappings = '';
        } else {
            this.updateFolderDefinitionsFromUI('series');
            p.SelectedSeriesCategoryIds = this.getAllCategoryIdsFromFolders('series');
            p.SeriesFolderMappings = this.buildFolderMappings(this.seriesFolderDefinitions);
        }

        // Persist per-item exclusions regardless of folder mode (empty = sync everything).
        p.ExcludedVodStreamIds = this.excludedVodStreamIds.slice();
        p.ExcludedSeriesIds = this.excludedSeriesIds.slice();

        p.TmdbFolderIdOverrides = document.getElementById('txtTmdbFolderIdOverrides').value;
        p.TvdbFolderIdOverrides = document.getElementById('txtTvdbFolderIdOverrides').value;

        p.FallbackToYearlessLookup = document.getElementById('chkFallbackToYearlessLookup').checked;

        var txtCustomTermsSave = document.getElementById('txtCustomTitleRemoveTerms');
        if (txtCustomTermsSave) p.CustomTitleRemoveTerms = txtCustomTermsSave.value;
        var txtRegexPatternsSave = document.getElementById('txtRegexRemovalPatterns');
        if (txtRegexPatternsSave) p.RegexRemovalPatterns = txtRegexPatternsSave.value;

        p.SyncParallelism = parseInt(document.getElementById('txtSyncParallelism').value) || 10;
        p.CategoryBatchSize = parseInt(document.getElementById('txtCategoryBatchSize').value) || 25;
        p.RequestDelayMs = parseInt(document.getElementById('txtRequestDelayMs').value) || 50;
        p.MaxRetries = parseInt(document.getElementById('txtMaxRetries').value) || 3;
        p.RetryDelayMs = parseInt(document.getElementById('txtRetryDelayMs').value) || 1000;

        p.DownloadArtworkForUnmatched = document.getElementById('chkDownloadArtworkForUnmatched').checked;
        p.EnableIncrementalSync = document.getElementById('chkEnableIncrementalSync').checked;
        p.FullSyncIntervalDays = parseInt(document.getElementById('txtFullSyncIntervalDays').value) || 7;
        p.FullSyncChangeThreshold = (parseInt(document.getElementById('txtFullSyncChangeThreshold').value) || 50) / 100;
        p.EnableProactiveMediaInfo = document.getElementById('chkEnableProactiveMediaInfo').checked;

        p.EnableDispatcharrMode = document.getElementById('chkEnableDispatcharrMode').checked;
        p.DispatcharrApiUser = document.getElementById('txtDispatcharrApiUser').value.trim();
        p.DispatcharrApiPass = document.getElementById('txtDispatcharrApiPass').value;
    },

    // Tab switching
    switchTab: function (tabName) {
        document.querySelectorAll('.xtream-tab').forEach(function (tab) {
            const isActive = tab.getAttribute('data-tab') === tabName;
            tab.classList.toggle('active', isActive);
            tab.setAttribute('aria-selected', isActive ? 'true' : 'false');
        });
        document.querySelectorAll('.xtream-tab-content').forEach(function (content) {
            content.classList.remove('active');
        });
        var tabContent = document.getElementById('tab-' + tabName);
        if (tabContent) {
            tabContent.classList.add('active');
        }

        if (tabName === 'dashboard') {
            this.loadDashboard();
        }
    },

    loadConfig: function () {
        Dashboard.showLoadingMsg();
        const self = this;

        ApiClient.getPluginConfiguration(this.pluginUniqueId).then(function (config) {
            // Load providers — migration produces at least one entry on first load
            if (config.Providers && config.Providers.length > 0) {
                self.providers = config.Providers;
            } else {
                self.providers = [self.makeDefaultProvider(0)];
            }
            self.activeProviderIndex = 0;
            self.renderProviderSelector();
            self.loadProviderIntoUI(0);

            // Global settings (not per-provider)
            document.getElementById('txtSyncInterval').value = config.SyncIntervalMinutes || 60;
            document.getElementById('chkTriggerScan').checked = config.TriggerLibraryScan === true;
            document.getElementById('chkEnableMetadataLookup').checked = config.EnableMetadataLookup !== false;
            document.getElementById('chkUseBetaChannel').checked = config.UseBetaChannel === true;
            document.getElementById('txtMetadataParallelism').value = config.MetadataParallelism || 3;

            // Schedule settings
            document.getElementById('selSyncScheduleType').value = config.SyncScheduleType || 'Interval';
            document.getElementById('selSyncDailyHour').value = config.SyncDailyHour || 3;
            document.getElementById('selSyncDailyMinute').value = config.SyncDailyMinute || 0;
            self.updateScheduleVisibility();

            // Live TV settings
            document.getElementById('chkEnableLiveTv').checked = config.EnableLiveTv || false;
            document.getElementById('chkEnableNativeTuner').checked = config.EnableNativeTuner || false;
            document.getElementById('chkEnableEpg').checked = config.EnableEpg !== false;
            document.getElementById('selLiveTvOutputFormat').value = config.LiveTvOutputFormat || 'ts';
            document.getElementById('chkIncludeAdultChannels').checked = config.IncludeAdultChannels || false;
            document.getElementById('txtM3UCacheMinutes').value = config.M3UCacheMinutes || 15;
            document.getElementById('txtEpgCacheMinutes').value = config.EpgCacheMinutes || 30;
            document.getElementById('txtEpgDaysToFetch').value = config.EpgDaysToFetch || 2;
            document.getElementById('txtEpgParallelism').value = config.EpgParallelism || 5;
            self.selectedLiveCategoryIds = config.SelectedLiveCategoryIds || [];
            self.excludedLiveStreamIds = config.ExcludedLiveStreamIds || [];

            // Live channel selection mode (IncludeAll | Custom). Backend defaults to IncludeAll on
            // fresh installs; existing configs with state are migrated to Custom on startup.
            var liveMode = config.LiveChannelMode || 'IncludeAll';
            var modeRadios = document.getElementsByName('LiveChannelMode');
            for (var i = 0; i < modeRadios.length; i++) {
                modeRadios[i].checked = (modeRadios[i].value === liveMode);
            }
            self.updateLiveChannelModeVisibility();

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

            // Live categories auto-load (uses provider 0 credentials). Only meaningful in Custom mode.
            var p0 = self.providers[0];
            if (p0 && p0.BaseUrl && p0.Username && liveMode === 'Custom') {
                self.loadLiveCategories();
            }
        });

        this.loadSyncStatus();
        this.checkRunningSync();
        this.loadDashboard();
    },

    saveConfig: function () {
        Dashboard.showLoadingMsg();
        const self = this;

        // Flush current UI state into providers array before saving
        self.updateActiveProviderFromUI();

        ApiClient.getPluginConfiguration(this.pluginUniqueId).then(function (config) {
            // Write providers array
            config.Providers = self.providers;

            // Global settings only
            config.SyncIntervalMinutes = parseInt(document.getElementById('txtSyncInterval').value) || 60;
            config.TriggerLibraryScan = document.getElementById('chkTriggerScan').checked;
            config.EnableMetadataLookup = document.getElementById('chkEnableMetadataLookup').checked;
            config.UseBetaChannel = document.getElementById('chkUseBetaChannel').checked;
            config.MetadataParallelism = parseInt(document.getElementById('txtMetadataParallelism').value) || 3;

            // Schedule settings
            config.SyncScheduleType = document.getElementById('selSyncScheduleType').value;
            config.SyncDailyHour = parseInt(document.getElementById('selSyncDailyHour').value) || 3;
            config.SyncDailyMinute = parseInt(document.getElementById('selSyncDailyMinute').value) || 0;

            // Live TV settings
            config.EnableLiveTv = document.getElementById('chkEnableLiveTv').checked;
            config.EnableNativeTuner = document.getElementById('chkEnableNativeTuner').checked;
            config.EnableEpg = document.getElementById('chkEnableEpg').checked;
            config.LiveTvOutputFormat = document.getElementById('selLiveTvOutputFormat').value;
            config.IncludeAdultChannels = document.getElementById('chkIncludeAdultChannels').checked;
            config.M3UCacheMinutes = parseInt(document.getElementById('txtM3UCacheMinutes').value) || 15;
            config.EpgCacheMinutes = parseInt(document.getElementById('txtEpgCacheMinutes').value) || 30;
            config.EpgDaysToFetch = parseInt(document.getElementById('txtEpgDaysToFetch').value) || 2;
            config.EpgParallelism = parseInt(document.getElementById('txtEpgParallelism').value) || 5;
            config.SelectedLiveCategoryIds = self.getSelectedCategoryIds('live');
            config.ExcludedLiveStreamIds = self.excludedLiveStreamIds.slice();

            var checkedMode = document.querySelector('input[name="LiveChannelMode"]:checked');
            config.LiveChannelMode = checkedMode ? checkedMode.value : 'IncludeAll';

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
        if (!container) {
            return;
        }
        var folderItems = container.querySelectorAll('.folder-item');

        definitions.length = 0; // Clear array

        folderItems.forEach(function (item, index) {
            var nameInput = item.querySelector('.folder-name-input');
            var checkboxes = item.querySelectorAll('input[type="checkbox"]:checked');
            var categoryIds = [];

            checkboxes.forEach(function (cb) {
                categoryIds.push(parseInt(cb.getAttribute('data-category-id')));
            });

            if (nameInput && nameInput.value.trim()) {
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

        const baseUrl = document.getElementById('txtBaseUrl').value.trim().replace(/\/$/, '');

        // Validate URL format
        try {
            new URL(baseUrl);
        } catch (e) {
            statusSpan.innerHTML = '<span style="color: red;">Invalid URL format. Must include protocol (http:// or https://)</span>';
            return;
        }

        const credentials = {
            BaseUrl: baseUrl,
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
        let html = '<br/><span style="color: orange;">';
        if (progress.MoviePhase || progress.SeriesPhase) {
            if (progress.MoviePhase) html += progress.MoviePhase;
            if (progress.SeriesPhase) {
                if (progress.MoviePhase) html += '<br/>';
                html += progress.SeriesPhase;
            }
        } else {
            html += progress.Phase;
        }
        if (progress.CurrentItem) {
            html += ': ' + this.escapeHtml(progress.CurrentItem);
        }
        if (progress.TotalCategories > 0) {
            html += '<br/>Batches: ' + progress.CategoriesProcessed + '/' + progress.TotalCategories;
        }
        if (progress.TotalItems > 0) {
            html += ' | Items: ' + progress.ItemsProcessed + '/' + progress.TotalItems;
        }
        if (progress.LiveTvPhase) {
            html += '<br/>' + this.escapeHtml(progress.LiveTvPhase);
        }
        const created = [];
        if (progress.MoviesCreated > 0) created.push(progress.MoviesCreated + ' movies');
        if (progress.EpisodesCreated > 0) created.push(progress.EpisodesCreated + ' episodes');
        if ((progress.ChannelsCreated || 0) > 0) created.push(progress.ChannelsCreated + ' channels');
        if (created.length > 0) {
            html += '<br/>Created: ' + created.join(', ');
        }
        const updated = [];
        if ((progress.MoviesUpdated || 0) > 0) updated.push(progress.MoviesUpdated + ' movies');
        if ((progress.EpisodesUpdated || 0) > 0) updated.push(progress.EpisodesUpdated + ' episodes');
        if ((progress.ChannelsUpdated || 0) > 0) updated.push(progress.ChannelsUpdated + ' channels');
        if (updated.length > 0) {
            html += '<br/>Updated: ' + updated.join(', ');
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

    checkRunningSync: function () {
        const self = this;
        fetch(ApiClient.getUrl('XtreamLibrary/Progress'), {
            method: 'GET',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (r) {
            return r.ok ? r.json() : null;
        }).then(function (progress) {
            if (progress && progress.IsRunning) {
                var syncBtn = document.getElementById('btnManualSync');
                self.isSyncing = true;
                syncBtn.querySelector('span').textContent = 'Cancel Sync';
                syncBtn.style.background = '#c0392b';
                self.displayProgress(progress);
                self.startProgressPolling();
                self.pollForCompletion();
            }
        }).catch(function () {});
    },

    formatDuration: function (startTime, endTime) {
        var start = new Date(startTime);
        var end = new Date(endTime);
        var ms = end - start;
        if (ms < 0) return '0s';
        var seconds = Math.floor(ms / 1000);
        var minutes = Math.floor(seconds / 60);
        var hours = Math.floor(minutes / 60);
        seconds = seconds % 60;
        minutes = minutes % 60;
        if (hours > 0) return hours + 'h ' + minutes + 'm ' + seconds + 's';
        if (minutes > 0) return minutes + 'm ' + seconds + 's';
        return seconds + 's';
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
        const duration = this.formatDuration(result.StartTime, result.EndTime);

        // Sync type badge
        var syncBadge = '';
        if (result.WasIncrementalSync) {
            syncBadge = '<span style="background: #1a5276; color: #85c1e9; padding: 2px 8px; border-radius: 4px; font-size: 0.85em; margin-left: 8px;">Incremental</span>';
        } else {
            syncBadge = '<span style="background: #1e3a1e; color: #82e0aa; padding: 2px 8px; border-radius: 4px; font-size: 0.85em; margin-left: 8px;">Full Sync</span>';
        }

        let html = '<strong>Last Sync:</strong> ' + startTime + ' - ' + status + syncBadge;
        html += '<br/><span style="color: #aaa;">Duration: ' + duration + '</span><br/><br/>';
        html += '<strong>Movies</strong><br/>';
        html += '&nbsp;&nbsp;Total: ' + (result.TotalMovies || (result.MoviesCreated + result.MoviesSkipped)) + '<br/>';
        html += '&nbsp;&nbsp;' + result.MoviesCreated + ' added' + ((result.MoviesUpdated || 0) > 0 ? ', ' + result.MoviesUpdated + ' updated' : '') + ', ' + (result.MoviesDeleted || 0) + ' deleted<br/><br/>';
        html += '<strong>Series</strong><br/>';
        html += '&nbsp;&nbsp;Total: ' + (result.TotalSeries || (result.SeriesCreated + result.SeriesSkipped) || 0) + '<br/>';
        html += '&nbsp;&nbsp;' + (result.SeriesCreated || 0) + ' added, ' + (result.SeriesDeleted || 0) + ' deleted<br/>';
        html += '&nbsp;&nbsp;Seasons: ' + (result.TotalSeasons || (result.SeasonsCreated + result.SeasonsSkipped) || 0) + ' total';
        html += ', ' + (result.SeasonsCreated || 0) + ' added, ' + (result.SeasonsDeleted || 0) + ' deleted<br/>';
        html += '&nbsp;&nbsp;Episodes: ' + (result.TotalEpisodes || (result.EpisodesCreated + result.EpisodesSkipped)) + ' total';
        html += ', ' + result.EpisodesCreated + ' added' + ((result.EpisodesUpdated || 0) > 0 ? ', ' + result.EpisodesUpdated + ' updated' : '') + ', ' + (result.EpisodesDeleted || 0) + ' deleted';

        // Live TV channel counts (shown when Live TV is enabled and any field is populated).
        var hasLiveTv = (result.TotalChannels || 0) > 0
            || (result.ChannelsCreated || 0) > 0
            || (result.ChannelsUpdated || 0) > 0
            || (result.ChannelsDeleted || 0) > 0
            || (result.ChannelsSkipped || 0) > 0;
        if (hasLiveTv) {
            html += '<br/><br/><strong>Live TV</strong><br/>';
            html += '&nbsp;&nbsp;Total: ' + (result.TotalChannels || ((result.ChannelsCreated || 0) + (result.ChannelsUpdated || 0) + (result.ChannelsSkipped || 0))) + ' channels<br/>';
            var parts = [];
            parts.push((result.ChannelsCreated || 0) + ' added');
            if ((result.ChannelsUpdated || 0) > 0) parts.push(result.ChannelsUpdated + ' updated');
            parts.push((result.ChannelsDeleted || 0) + ' deleted');
            html += '&nbsp;&nbsp;' + parts.join(', ');
        }

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

        fetch(ApiClient.getUrl('XtreamLibrary/Categories/Vod') + '?providerIndex=' + this.activeProviderIndex, {
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

        fetch(ApiClient.getUrl('XtreamLibrary/Categories/Series') + '?providerIndex=' + this.activeProviderIndex, {
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
            if (type === 'live') {
                const isExpanded = !!self.expandedLiveCategories[category.CategoryId];
                html += '<div class="live-cat-row" data-cat-id="' + category.CategoryId + '" data-category-name="' + self.escapeHtml(category.CategoryName) + '">';
                html += '<div style="display: flex; align-items: center;">';
                html += '<button type="button" class="live-cat-expand" data-cat-id="' + category.CategoryId + '" ';
                html += 'aria-label="Toggle channels" ';
                html += 'style="margin-right: 8px; background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; font-size: 0.85em; padding: 2px 8px; min-width: 24px; border-radius: 3px;">';
                html += isExpanded ? '▾' : '▸';
                html += '</button>';
                html += '<label class="emby-checkbox-label" style="flex: 1;">';
                html += '<input is="emby-checkbox" type="checkbox" id="' + checkboxId + '" ';
                html += 'data-category-id="' + category.CategoryId + '" data-category-type="' + type + '" ';
                html += 'data-index="' + index + '" ' + isChecked + '/>';
                html += '<span>' + self.escapeHtml(category.CategoryName) + ' <small style="opacity:0.5;">(ID: ' + category.CategoryId + ')</small></span>';
                html += '</label>';
                html += '</div>';
                html += '<div class="live-channel-list" data-cat-id="' + category.CategoryId + '" ';
                html += 'style="margin-left: 36px; margin-top: 4px; ' + (isExpanded ? '' : 'display: none;') + '"></div>';
                html += '</div>';
            } else {
                // vod / series: expandable row with a per-item panel (issue #54)
                const isExpanded = !!(self.expandedContentCategories[type] && self.expandedContentCategories[type][category.CategoryId]);
                html += '<div class="content-cat-row" data-cat-id="' + category.CategoryId + '" data-content-type="' + type + '">';
                html += '<div style="display: flex; align-items: center;">';
                html += '<button type="button" class="content-cat-expand" data-cat-id="' + category.CategoryId + '" data-content-type="' + type + '" ';
                html += 'aria-label="Toggle items" ';
                html += 'style="margin-right: 8px; background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; font-size: 0.85em; padding: 2px 8px; min-width: 24px; border-radius: 3px;">';
                html += isExpanded ? '▾' : '▸';
                html += '</button>';
                html += '<label class="emby-checkbox-label" style="flex: 1;">';
                html += '<input is="emby-checkbox" type="checkbox" id="' + checkboxId + '" ';
                html += 'data-category-id="' + category.CategoryId + '" data-category-type="' + type + '" ';
                html += 'data-index="' + index + '" ' + isChecked + '/>';
                html += '<span>' + self.escapeHtml(category.CategoryName) + ' <small style="opacity:0.5;">(ID: ' + category.CategoryId + ')</small></span>';
                html += '</label>';
                html += '</div>';
                html += '<div class="content-item-list" data-cat-id="' + category.CategoryId + '" data-content-type="' + type + '" ';
                html += 'style="margin-left: 36px; margin-top: 4px; ' + (isExpanded ? '' : 'display: none;') + '"></div>';
                html += '</div>';
            }
        });

        container.innerHTML = html;

        // Add shift+click range selection support — scoped to category checkboxes only.
        const checkboxes = container.querySelectorAll('input[data-category-type="' + type + '"]');
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
                    // Programmatic check assignment skips the 'change' event, so the counter
                    // needs an explicit nudge to reflect the range selection.
                    if (type === 'live') {
                        self.updateLiveCategoryCounter();
                    }
                }
                self.lastClickedIndex[type] = currentIndex;
            });
        });

        if (type === 'live') {
            container.querySelectorAll('.live-cat-expand').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    const catId = parseInt(btn.getAttribute('data-cat-id'));
                    self.toggleLiveCategory(catId, btn);
                });
            });

            // Update the counter whenever a category checkbox changes (including via shift+click,
            // since the synthetic state changes in the click handler don't fire change events).
            // Also re-render any expanded child panel so per-channel checkboxes track the new
            // category state (see renderLiveChannels for the categorySelected gating).
            checkboxes.forEach(function (cb) {
                cb.addEventListener('change', function () {
                    self.updateLiveCategoryCounter();
                    const catId = parseInt(cb.getAttribute('data-category-id'));
                    if (self.expandedLiveCategories[catId] && self.liveChannelsByCategory[catId]) {
                        self.renderLiveChannels(catId);
                    }
                });
            });

            // Auto-render channels for categories that were expanded before this re-render.
            Object.keys(self.expandedLiveCategories).forEach(function (catIdStr) {
                if (!self.expandedLiveCategories[catIdStr]) return;
                const catId = parseInt(catIdStr);
                const channels = self.liveChannelsByCategory[catId];
                if (channels) {
                    self.renderLiveChannels(catId);
                } else {
                    self.fetchLiveChannelsForCategory(catId);
                }
            });

            self.updateLiveCategoryCounter();
        }

        if (type === 'vod' || type === 'series') {
            container.querySelectorAll('.content-cat-expand').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    const catId = parseInt(btn.getAttribute('data-cat-id'));
                    self.toggleContentCategory(type, catId, btn);
                });
            });

            // When a category checkbox toggles, re-render any expanded panel so item
            // checkboxes follow the category's selected state.
            checkboxes.forEach(function (cb) {
                cb.addEventListener('change', function () {
                    const catId = parseInt(cb.getAttribute('data-category-id'));
                    if (self.expandedContentCategories[type][catId] && self.contentItemsByCategory[type][catId]) {
                        self.renderContentItems(type, catId);
                    }
                });
            });

            // Re-render panels that were expanded before this list was rebuilt.
            Object.keys(self.expandedContentCategories[type]).forEach(function (catIdStr) {
                if (!self.expandedContentCategories[type][catIdStr]) return;
                const catId = parseInt(catIdStr);
                if (self.contentItemsByCategory[type][catId]) {
                    self.renderContentItems(type, catId);
                } else {
                    self.fetchContentItemsForCategory(type, catId);
                }
            });
        }
    },

    toggleLiveCategory: function (categoryId, btn) {
        const self = this;
        const panel = document.querySelector('.live-channel-list[data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        const isExpanded = !!self.expandedLiveCategories[categoryId];
        if (isExpanded) {
            self.expandedLiveCategories[categoryId] = false;
            panel.style.display = 'none';
            if (btn) btn.textContent = '▸';
            return;
        }

        self.expandedLiveCategories[categoryId] = true;
        panel.style.display = '';
        if (btn) btn.textContent = '▾';

        if (self.liveChannelsByCategory[categoryId]) {
            self.renderLiveChannels(categoryId);
        } else {
            self.fetchLiveChannelsForCategory(categoryId);
        }
    },

    fetchLiveChannelsForCategory: function (categoryId) {
        const self = this;
        const panel = document.querySelector('.live-channel-list[data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        panel.innerHTML = '<div class="fieldDescription" style="padding: 4px 0;">Loading channels...</div>';

        fetch(ApiClient.getUrl('XtreamLibrary/Channels/Live?categoryId=' + encodeURIComponent(categoryId)), {
            headers: { 'X-Emby-Token': ApiClient.accessToken() }
        })
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (channels) {
                self.liveChannelsByCategory[categoryId] = channels || [];
                self.renderLiveChannels(categoryId);
            })
            .catch(function (error) {
                console.error('Failed to load channels for category ' + categoryId + ':', error);
                panel.innerHTML = '<div style="color: #d33; padding: 4px 0;">Failed to load channels. ' +
                    '<button type="button" class="live-channel-retry" data-cat-id="' + categoryId + '" ' +
                    'style="background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; padding: 2px 8px; margin-left: 8px; border-radius: 3px;">Retry</button></div>';
                const retry = panel.querySelector('.live-channel-retry');
                if (retry) {
                    retry.addEventListener('click', function () {
                        self.fetchLiveChannelsForCategory(categoryId);
                    });
                }
            });
    },

    renderLiveChannels: function (categoryId) {
        const self = this;
        const panel = document.querySelector('.live-channel-list[data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        const channels = self.liveChannelsByCategory[categoryId] || [];
        if (channels.length === 0) {
            panel.innerHTML = '<div class="fieldDescription" style="padding: 4px 0;">No channels in this category.</div>';
            return;
        }

        const excluded = {};
        self.excludedLiveStreamIds.forEach(function (id) { excluded[id] = true; });

        // If the parent category isn't selected, the whole category is skipped at sync time —
        // reflect that in the UI so per-channel checkboxes don't show as ticked when they
        // won't actually sync. Without this, Deselect-All-Categories clears the exclusion
        // list and every channel underneath would pop back on as "checked", which is what
        // Estarna reported on issue #46 against v1.35.1.
        const categoryCb = document.querySelector(
            'input[data-category-type="live"][data-category-id="' + categoryId + '"]');
        const categorySelected = categoryCb ? categoryCb.checked : false;

        let html = '';
        html += '<div style="margin: 4px 0;">';
        html += '<button type="button" class="live-ch-select-all" data-cat-id="' + categoryId + '" ';
        html += 'style="background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; padding: 2px 8px; border-radius: 3px; margin-right: 6px;">Select all</button>';
        html += '<button type="button" class="live-ch-deselect-all" data-cat-id="' + categoryId + '" ';
        html += 'style="background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; padding: 2px 8px; border-radius: 3px;">Deselect all</button>';
        html += '<small style="opacity: 0.6; margin-left: 10px;">' + channels.length + ' channels</small>';
        if (!categorySelected) {
            html += '<small style="opacity: 0.6; margin-left: 10px; font-style: italic;">Category is deselected — tick a channel or the category to include it.</small>';
        }
        html += '</div>';

        channels.forEach(function (channel) {
            const isChecked = categorySelected && !excluded[channel.StreamId] ? 'checked' : '';
            html += '<div class="checkboxContainer" style="margin: 2px 0;">';
            html += '<label class="emby-checkbox-label">';
            html += '<input is="emby-checkbox" type="checkbox" class="live-channel-cb" ';
            html += 'data-stream-id="' + channel.StreamId + '" ' + isChecked + '/>';
            html += '<span>' + self.escapeHtml(channel.Name || '(unnamed)');
            if (channel.Num) {
                html += ' <small style="opacity:0.5;">#' + channel.Num + '</small>';
            }
            html += '</span></label></div>';
        });

        panel.innerHTML = html;

        panel.querySelectorAll('.live-channel-cb').forEach(function (cb) {
            cb.addEventListener('change', function () {
                const streamId = parseInt(cb.getAttribute('data-stream-id'));
                // Ticking a channel under a deselected category implies the user wants the
                // category included — auto-tick the parent so the channel actually syncs.
                if (cb.checked && categoryCb && !categoryCb.checked) {
                    categoryCb.checked = true;
                    self.updateLiveCategoryCounter();
                }
                self.updateLiveExclusion(streamId, !cb.checked);
            });
        });

        const selectAll = panel.querySelector('.live-ch-select-all');
        if (selectAll) {
            selectAll.addEventListener('click', function () {
                if (categoryCb && !categoryCb.checked) {
                    categoryCb.checked = true;
                    self.updateLiveCategoryCounter();
                }
                panel.querySelectorAll('.live-channel-cb').forEach(function (cb) {
                    if (!cb.checked) {
                        cb.checked = true;
                        self.updateLiveExclusion(parseInt(cb.getAttribute('data-stream-id')), false);
                    }
                });
            });
        }
        const deselectAll = panel.querySelector('.live-ch-deselect-all');
        if (deselectAll) {
            deselectAll.addEventListener('click', function () {
                panel.querySelectorAll('.live-channel-cb').forEach(function (cb) {
                    if (cb.checked) {
                        cb.checked = false;
                        self.updateLiveExclusion(parseInt(cb.getAttribute('data-stream-id')), true);
                    }
                });
            });
        }
    },

    updateLiveExclusion: function (streamId, shouldBeExcluded) {
        const self = this;
        const idx = self.excludedLiveStreamIds.indexOf(streamId);
        if (shouldBeExcluded && idx === -1) {
            self.excludedLiveStreamIds.push(streamId);
        } else if (!shouldBeExcluded && idx !== -1) {
            self.excludedLiveStreamIds.splice(idx, 1);
        }
    },

    // ----- Per-item VOD/Series selection (issue #54), mirroring the Live TV per-channel UI -----

    toggleContentCategory: function (type, categoryId, btn) {
        const self = this;
        const panel = document.querySelector('.content-item-list[data-content-type="' + type + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        const isExpanded = !!self.expandedContentCategories[type][categoryId];
        if (isExpanded) {
            self.expandedContentCategories[type][categoryId] = false;
            panel.style.display = 'none';
            if (btn) btn.textContent = '▸';
            return;
        }

        self.expandedContentCategories[type][categoryId] = true;
        panel.style.display = '';
        if (btn) btn.textContent = '▾';

        if (self.contentItemsByCategory[type][categoryId]) {
            self.renderContentItems(type, categoryId);
        } else {
            self.fetchContentItemsForCategory(type, categoryId);
        }
    },

    fetchContentItemsForCategory: function (type, categoryId) {
        const self = this;
        const panel = document.querySelector('.content-item-list[data-content-type="' + type + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        const endpoint = type === 'vod' ? 'XtreamLibrary/Streams/Vod' : 'XtreamLibrary/Series/List';
        const label = type === 'vod' ? 'movies' : 'series';
        panel.innerHTML = '<div class="fieldDescription" style="padding: 4px 0;">Loading ' + label + '...</div>';

        fetch(ApiClient.getUrl(endpoint) + '?categoryId=' + encodeURIComponent(categoryId) + '&providerIndex=' + self.activeProviderIndex, {
            headers: { 'X-Emby-Token': ApiClient.accessToken() }
        })
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (items) {
                self.contentItemsByCategory[type][categoryId] = items || [];
                self.renderContentItems(type, categoryId);
            })
            .catch(function (error) {
                console.error('Failed to load ' + label + ' for category ' + categoryId + ':', error);
                panel.innerHTML = '<div style="color: #d33; padding: 4px 0;">Failed to load ' + label + '. ' +
                    '<button type="button" class="content-item-retry" data-cat-id="' + categoryId + '" data-content-type="' + type + '" ' +
                    'style="background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; padding: 2px 8px; margin-left: 8px; border-radius: 3px;">Retry</button></div>';
                const retry = panel.querySelector('.content-item-retry');
                if (retry) {
                    retry.addEventListener('click', function () {
                        self.fetchContentItemsForCategory(type, categoryId);
                    });
                }
            });
    },

    renderContentItems: function (type, categoryId) {
        const self = this;
        const panel = document.querySelector('.content-item-list[data-content-type="' + type + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        const items = self.contentItemsByCategory[type][categoryId] || [];
        const label = type === 'vod' ? 'movies' : 'series';
        if (items.length === 0) {
            panel.innerHTML = '<div class="fieldDescription" style="padding: 4px 0;">No ' + label + ' in this category.</div>';
            return;
        }

        const idField = type === 'vod' ? 'StreamId' : 'SeriesId';
        const exclusionList = type === 'vod' ? self.excludedVodStreamIds : self.excludedSeriesIds;
        const excluded = {};
        exclusionList.forEach(function (id) { excluded[id] = true; });

        const categoryCb = document.querySelector('input[data-category-type="' + type + '"][data-category-id="' + categoryId + '"]');
        const categorySelected = categoryCb ? categoryCb.checked : false;

        let html = '';
        html += '<div style="margin: 4px 0;">';
        html += '<button type="button" class="content-item-select-all" data-cat-id="' + categoryId + '" data-content-type="' + type + '" ';
        html += 'style="background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; padding: 2px 8px; border-radius: 3px; margin-right: 6px;">Select all</button>';
        html += '<button type="button" class="content-item-deselect-all" data-cat-id="' + categoryId + '" data-content-type="' + type + '" ';
        html += 'style="background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; padding: 2px 8px; border-radius: 3px;">Deselect all</button>';
        html += '<small style="opacity: 0.6; margin-left: 10px;">' + items.length + ' ' + label + '</small>';
        if (!categorySelected) {
            html += '<small style="opacity: 0.6; margin-left: 10px; font-style: italic;">Category is deselected — tick an item or the category to include it.</small>';
        }
        html += '</div>';

        items.forEach(function (item) {
            const itemId = item[idField];
            const isChecked = categorySelected && !excluded[itemId] ? 'checked' : '';
            html += '<div class="checkboxContainer" style="margin: 2px 0;">';
            html += '<label class="emby-checkbox-label">';
            html += '<input is="emby-checkbox" type="checkbox" class="content-item-cb" ';
            html += 'data-item-id="' + itemId + '" ' + isChecked + '/>';
            html += '<span>' + self.escapeHtml(item.Name || '(unnamed)');
            if (item.Num) {
                html += ' <small style="opacity:0.5;">#' + item.Num + '</small>';
            }
            html += '</span></label></div>';
        });

        panel.innerHTML = html;

        panel.querySelectorAll('.content-item-cb').forEach(function (cb) {
            cb.addEventListener('change', function () {
                const itemId = parseInt(cb.getAttribute('data-item-id'));
                if (cb.checked && categoryCb && !categoryCb.checked) {
                    categoryCb.checked = true;
                }
                self.updateContentExclusion(type, itemId, !cb.checked);
            });
        });

        const selectAll = panel.querySelector('.content-item-select-all');
        if (selectAll) {
            selectAll.addEventListener('click', function () {
                if (categoryCb && !categoryCb.checked) {
                    categoryCb.checked = true;
                }
                panel.querySelectorAll('.content-item-cb').forEach(function (cb) {
                    if (!cb.checked) {
                        cb.checked = true;
                        self.updateContentExclusion(type, parseInt(cb.getAttribute('data-item-id')), false);
                    }
                });
            });
        }
        const deselectAll = panel.querySelector('.content-item-deselect-all');
        if (deselectAll) {
            deselectAll.addEventListener('click', function () {
                panel.querySelectorAll('.content-item-cb').forEach(function (cb) {
                    if (cb.checked) {
                        cb.checked = false;
                        self.updateContentExclusion(type, parseInt(cb.getAttribute('data-item-id')), true);
                    }
                });
            });
        }
    },

    updateContentExclusion: function (type, itemId, shouldBeExcluded) {
        const self = this;
        const list = type === 'vod' ? self.excludedVodStreamIds : self.excludedSeriesIds;
        const idx = list.indexOf(itemId);
        if (shouldBeExcluded && idx === -1) {
            list.push(itemId);
        } else if (!shouldBeExcluded && idx !== -1) {
            list.splice(idx, 1);
        }
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
        const self = this;
        const checkboxes = document.querySelectorAll('input[data-category-type="' + type + '"]');
        checkboxes.forEach(function (checkbox) {
            checkbox.checked = true;
        });
        if (type === 'live') {
            // Master "Select all" implies a clean slate: drop any per-channel exclusions so
            // the user gets every channel from every category. Existing expanded panels
            // need redrawing so the channel checkboxes follow the new state.
            self.excludedLiveStreamIds = [];
            self.redrawExpandedLiveChannelPanels();
            self.updateLiveCategoryCounter();
        }
    },

    deselectAllCategories: function (type) {
        const self = this;
        const checkboxes = document.querySelectorAll('input[data-category-type="' + type + '"]');
        checkboxes.forEach(function (checkbox) {
            checkbox.checked = false;
        });
        if (type === 'live') {
            // Same clean-slate logic as Select all — the per-channel exclusion list is
            // meaningless when no category is selected, and leaving stale state behind was
            // the root of the "Deselect All seems broken" report.
            self.excludedLiveStreamIds = [];
            self.redrawExpandedLiveChannelPanels();
            self.updateLiveCategoryCounter();
        }
    },

    clearChannelExclusions: function () {
        const self = this;
        self.excludedLiveStreamIds = [];
        self.redrawExpandedLiveChannelPanels();
        self.updateLiveCategoryCounter();
    },

    redrawExpandedLiveChannelPanels: function () {
        const self = this;
        Object.keys(self.expandedLiveCategories).forEach(function (categoryId) {
            if (self.expandedLiveCategories[categoryId]) {
                self.renderLiveChannels(parseInt(categoryId));
            }
        });
    },

    updateLiveCategoryCounter: function () {
        const counter = document.getElementById('liveCategoryCounter');
        if (!counter) return;
        const all = document.querySelectorAll('input[data-category-type="live"]');
        const checked = document.querySelectorAll('input[data-category-type="live"]:checked');
        counter.textContent = checked.length + ' of ' + all.length + ' categories selected';
    },

    updateLiveChannelModeVisibility: function () {
        const checked = document.querySelector('input[name="LiveChannelMode"]:checked');
        const mode = checked ? checked.value : 'IncludeAll';
        const customSection = document.getElementById('liveCustomSection');
        if (customSection) {
            customSection.style.display = (mode === 'Custom') ? '' : 'none';
        }
    },

    filterLiveCategoryList: function () {
        const input = document.getElementById('liveCategorySearch');
        if (!input) return;
        const query = input.value.trim().toLowerCase();
        const items = document.querySelectorAll('#liveCategoryList .live-cat-row');
        items.forEach(function (item) {
            const name = (item.getAttribute('data-category-name') || '').toLowerCase();
            item.style.display = (query === '' || name.indexOf(query) !== -1) ? '' : 'none';
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

    updateDispatcharrVisibility: function () {
        const enabled = document.getElementById('chkEnableDispatcharrMode').checked;
        document.getElementById('dispatcharrSettings').style.display = enabled ? 'block' : 'none';
    },

    testDispatcharr: function () {
        const statusSpan = document.getElementById('dispatcharrStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Testing...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/TestDispatcharr') + '?providerIndex=' + this.activeProviderIndex, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (response) {
            return response.json();
        }).then(function (result) {
            if (result.Success) {
                statusSpan.innerHTML = '<span style="color: #52b54b;">' + result.Message + '</span>';
            } else {
                statusSpan.innerHTML = '<span style="color: #ff6b6b;">' + result.Message + '</span>';
            }
        }).catch(function (err) {
            statusSpan.innerHTML = '<span style="color: #ff6b6b;">Error: ' + err.message + '</span>';
        });
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

    cleanMovies: function () {
        if (!confirm('Are you sure you want to delete ALL Movies content?\n\nThis action cannot be undone.')) {
            return;
        }

        this.cleanLibraryFolder('CleanMovies');
    },

    cleanSeries: function () {
        if (!confirm('Are you sure you want to delete ALL Series content?\n\nThis action cannot be undone.')) {
            return;
        }

        this.cleanLibraryFolder('CleanSeries');
    },

    cleanLibraryFolder: function (endpoint) {
        const statusDiv = document.getElementById('cleanLibrariesStatus');
        statusDiv.innerHTML = '<span style="color: orange;">Deleting...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/' + endpoint) + '?providerIndex=' + XtreamLibraryConfig.activeProviderIndex, {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            if (data.Success) {
                statusDiv.innerHTML = '<span style="color: green;">' + data.Message + '</span>';
            } else {
                statusDiv.innerHTML = '<span style="color: red;">' + (data.Message || 'Failed to clean library.') + '</span>';
            }
        }).catch(function (error) {
            console.error('Clean library error:', error);
            statusDiv.innerHTML = '<span style="color: red;">Failed: ' + (error.message || 'Check console for details') + '</span>';
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

    toggleAdvanced: function () {
        var section = document.getElementById('advancedSettings');
        var arrow = document.getElementById('advancedArrow');
        if (section.style.display === 'none') {
            section.style.display = 'block';
            arrow.innerHTML = '&#9662;'; // ▾
        } else {
            section.style.display = 'none';
            arrow.innerHTML = '&#9656;'; // ▸
        }
    },

    // Dashboard methods
    loadDashboard: function () {
        var self = this;
        fetch(ApiClient.getUrl('XtreamLibrary/Dashboard'), {
            method: 'GET',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (r) {
            return r.ok ? r.json() : null;
        }).then(function (data) {
            if (!data) return;
            self.renderDashboardStatus(data.LastSync, data.Progress);
            self.renderDashboardSchedule(data);
            self.renderLibraryStats(data.LibraryStats);
            self.renderDashboardHistory(data.History);

            if (data.Progress && data.Progress.IsRunning) {
                self.showDashboardProgress(data.Progress);
                self.startDashboardProgressPolling();
                self.updateDashboardSyncButton(true);
            } else {
                self.hideDashboardProgress();
                self.stopDashboardProgressPolling();
                self.updateDashboardSyncButton(false);
            }

            // Show/hide retry button
            var btnRetry = document.getElementById('btnDashboardRetry');
            var failedCount = document.getElementById('dashboardFailedCount');
            if (btnRetry && data.LastSync && data.LastSync.FailedItems && data.LastSync.FailedItems.length > 0) {
                btnRetry.style.display = 'inline-block';
                if (failedCount) failedCount.textContent = data.LastSync.FailedItems.length;
            } else if (btnRetry) {
                btnRetry.style.display = 'none';
            }
        }).catch(function (err) {
            console.error('Dashboard load error:', err);
        });
    },

    renderDashboardStatus: function (lastSync, progress) {
        var container = document.getElementById('dashboardSyncStatus');
        if (!container) return;

        if (!lastSync) {
            container.innerHTML = '<span style="opacity: 0.5;">No sync has been performed yet.</span>';
            return;
        }

        var statusBadge = lastSync.Success
            ? '<span class="status-badge status-badge-success">Success</span>'
            : '<span class="status-badge status-badge-failed">Failed</span>';

        var typeBadge = lastSync.WasIncrementalSync
            ? '<span class="status-badge status-badge-incremental">Incremental</span>'
            : '<span class="status-badge status-badge-full">Full Sync</span>';

        if (progress && progress.IsRunning) {
            statusBadge = '<span class="status-badge status-badge-running">Running</span>';
        }

        var duration = this.formatDuration(lastSync.StartTime, lastSync.EndTime);
        var time = new Date(lastSync.StartTime).toLocaleString();

        var html = '<div style="margin-bottom: 10px;">' + statusBadge + ' ' + typeBadge + '</div>';
        html += '<div style="opacity: 0.7; margin-bottom: 10px;">' + time + ' &middot; ' + duration + '</div>';

        // Stat counters
        html += '<div>';
        html += this.renderStatBadge(lastSync.TotalMovies || (lastSync.MoviesCreated + lastSync.MoviesSkipped), 'Movies');
        html += this.renderStatBadge(lastSync.TotalSeries || (lastSync.SeriesCreated + (lastSync.SeriesSkipped || 0)), 'Series');
        html += this.renderStatBadge(lastSync.TotalEpisodes || (lastSync.EpisodesCreated + lastSync.EpisodesSkipped), 'Episodes');
        html += this.renderStatBadge(lastSync.MoviesCreated + (lastSync.SeriesCreated || 0) + lastSync.EpisodesCreated, 'Created');
        if (lastSync.Errors > 0) {
            html += '<div class="dashboard-stat" style="border: 1px solid rgba(255,100,100,0.3);">';
            html += '<span class="stat-value" style="color: #e08282;">' + lastSync.Errors + '</span>';
            html += '<span class="stat-label">Errors</span></div>';
        }
        html += '</div>';

        container.innerHTML = html;
    },

    renderStatBadge: function (value, label) {
        return '<div class="dashboard-stat"><span class="stat-value">' + (value || 0) + '</span><span class="stat-label">' + label + '</span></div>';
    },

    renderDashboardSchedule: function (data) {
        var container = document.getElementById('dashboardSchedule');
        if (!container) return;

        var html = '<div style="margin-bottom: 8px;"><strong>Type:</strong> ' + this.escapeHtml(data.ScheduleType) + '</div>';
        if (data.NextSyncTime) {
            html += '<div><strong>Next sync:</strong> ' + this.escapeHtml(data.NextSyncDisplay);
            html += '<br/><span style="opacity: 0.5; font-size: 0.9em;">' + new Date(data.NextSyncTime).toLocaleString() + '</span></div>';
        } else {
            html += '<div style="opacity: 0.5;">Next sync time unavailable (no previous sync)</div>';
        }

        container.innerHTML = html;
    },

    renderLibraryStats: function (stats) {
        var container = document.getElementById('dashboardLibraryStats');
        if (!container) return;

        if (!stats || (stats.TotalMovieFolders === 0 && stats.TotalSeriesFolders === 0)) {
            container.innerHTML = '<span style="opacity: 0.5;">No library content found. Run a sync first.</span>';
            return;
        }

        var html = '';

        if (stats.TotalMovieFolders > 0) {
            var moviePct = Math.round((stats.MatchedMovies / stats.TotalMovieFolders) * 100);
            html += '<div class="library-stat-bar">';
            html += '<span style="width: 80px;">Movies</span>';
            html += '<div class="bar-container"><div class="bar-fill" style="width: ' + moviePct + '%;"></div></div>';
            html += '<span style="width: 120px; text-align: right;">' + stats.MatchedMovies + ' / ' + stats.TotalMovieFolders + ' (' + moviePct + '%)</span>';
            html += '</div>';
            if (stats.UnmatchedMovies > 0) {
                html += '<div style="opacity: 0.5; font-size: 0.85em; margin-left: 80px;">' + stats.UnmatchedMovies + ' unmatched</div>';
            }
        }

        if (stats.TotalSeriesFolders > 0) {
            var seriesPct = Math.round((stats.MatchedSeries / stats.TotalSeriesFolders) * 100);
            html += '<div class="library-stat-bar" style="margin-top: 8px;">';
            html += '<span style="width: 80px;">Series</span>';
            html += '<div class="bar-container"><div class="bar-fill" style="width: ' + seriesPct + '%;"></div></div>';
            html += '<span style="width: 120px; text-align: right;">' + stats.MatchedSeries + ' / ' + stats.TotalSeriesFolders + ' (' + seriesPct + '%)</span>';
            html += '</div>';
            if (stats.UnmatchedSeries > 0) {
                html += '<div style="opacity: 0.5; font-size: 0.85em; margin-left: 80px;">' + stats.UnmatchedSeries + ' unmatched</div>';
            }
        }

        container.innerHTML = html;
    },

    renderDashboardHistory: function (history) {
        var container = document.getElementById('dashboardHistory');
        if (!container) return;

        if (!history || history.length === 0) {
            container.innerHTML = '<span style="opacity: 0.5;">No sync history yet.</span>';
            return;
        }

        var self = this;
        var html = '<table class="dashboard-history-table">';
        html += '<thead><tr><th>Time</th><th>Status</th><th>Type</th><th>Duration</th><th></th><th>Added</th><th>Deleted</th><th>Errors</th></tr></thead>';
        html += '<tbody>';

        var colorNum = function (val, color) {
            return val > 0 ? '<span style="color: ' + color + ';">' + val + '</span>' : '0';
        };

        history.forEach(function (entry) {
            var time = new Date(entry.StartTime).toLocaleString();
            var statusBadge = entry.Success
                ? '<span class="status-badge status-badge-success">OK</span>'
                : '<span class="status-badge status-badge-failed">Fail</span>';
            var typeBadge = entry.WasIncrementalSync ? 'Incr' : 'Full';
            var duration = self.formatDuration(entry.StartTime, entry.EndTime);
            var errors = entry.Errors || 0;

            // Movies row
            html += '<tr>';
            html += '<td rowspan="2" style="white-space: nowrap; vertical-align: middle;">' + time + '</td>';
            html += '<td rowspan="2" style="vertical-align: middle;">' + statusBadge + '</td>';
            html += '<td rowspan="2" style="vertical-align: middle;">' + typeBadge + '</td>';
            html += '<td rowspan="2" style="vertical-align: middle;">' + duration + '</td>';
            html += '<td style="opacity: 0.6; font-size: 0.85em;">Movies</td>';
            html += '<td>' + colorNum(entry.MoviesCreated || 0, '#82e0aa') + '</td>';
            html += '<td>' + colorNum(entry.MoviesDeleted || 0, '#e0c882') + '</td>';
            html += '<td rowspan="2" style="vertical-align: middle;">' + colorNum(errors, '#e08282') + '</td>';
            html += '</tr>';

            // Series row
            html += '<tr>';
            html += '<td style="opacity: 0.6; font-size: 0.85em;">Series</td>';
            html += '<td>' + colorNum((entry.SeriesCreated || 0) + (entry.EpisodesCreated || 0), '#82e0aa') + '</td>';
            html += '<td>' + colorNum((entry.SeriesDeleted || 0) + (entry.EpisodesDeleted || 0), '#e0c882') + '</td>';
            html += '</tr>';
        });

        html += '</tbody></table>';
        container.innerHTML = html;
    },

    showDashboardProgress: function (progress) {
        var section = document.getElementById('dashboardProgressSection');
        var content = document.getElementById('dashboardProgressContent');
        if (!section || !content) return;

        section.style.display = 'block';

        var percentage = 0;
        if (progress.TotalItems > 0) {
            percentage = Math.round((progress.ItemsProcessed / progress.TotalItems) * 100);
        }

        var html = '';

        // Phase text
        var phase = '';
        if (progress.MoviePhase || progress.SeriesPhase) {
            if (progress.MoviePhase) phase += progress.MoviePhase;
            if (progress.SeriesPhase) {
                if (progress.MoviePhase) phase += ' | ';
                phase += progress.SeriesPhase;
            }
        } else {
            phase = progress.Phase || '';
        }
        html += '<div style="margin-bottom: 6px;">' + this.escapeHtml(phase) + '</div>';

        // Progress bar
        html += '<div class="dashboard-progress-bar"><div class="dashboard-progress-fill" style="width: ' + percentage + '%;"></div></div>';

        // Details
        var details = [];
        if (progress.TotalCategories > 0) {
            details.push('Batches: ' + progress.CategoriesProcessed + '/' + progress.TotalCategories);
        }
        if (progress.TotalItems > 0) {
            details.push('Items: ' + progress.ItemsProcessed + '/' + progress.TotalItems + ' (' + percentage + '%)');
        }
        if (progress.CurrentItem) {
            details.push(this.escapeHtml(progress.CurrentItem));
        }
        if (details.length > 0) {
            html += '<div style="opacity: 0.7; font-size: 0.9em;">' + details.join(' &middot; ') + '</div>';
        }

        // Live counters
        var counters = [];
        if (progress.MoviesCreated > 0) counters.push(progress.MoviesCreated + ' movies created');
        if ((progress.MoviesUpdated || 0) > 0) counters.push(progress.MoviesUpdated + ' movies updated');
        if (progress.EpisodesCreated > 0) counters.push(progress.EpisodesCreated + ' episodes created');
        if ((progress.EpisodesUpdated || 0) > 0) counters.push(progress.EpisodesUpdated + ' episodes updated');
        if (counters.length > 0) {
            html += '<div style="margin-top: 6px; color: #82e0aa; font-size: 0.9em;">' + counters.join(' &middot; ') + '</div>';
        }

        content.innerHTML = html;
    },

    hideDashboardProgress: function () {
        var section = document.getElementById('dashboardProgressSection');
        if (section) section.style.display = 'none';
    },

    startDashboardProgressPolling: function () {
        var self = this;
        self.stopDashboardProgressPolling();

        self.dashboardProgressInterval = setInterval(function () {
            fetch(ApiClient.getUrl('XtreamLibrary/Progress'), {
                method: 'GET',
                headers: {
                    'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
                }
            }).then(function (r) {
                return r.ok ? r.json() : null;
            }).then(function (progress) {
                if (progress && progress.IsRunning) {
                    self.showDashboardProgress(progress);
                } else {
                    // Sync finished
                    self.hideDashboardProgress();
                    self.stopDashboardProgressPolling();
                    self.updateDashboardSyncButton(false);
                    var actionSpan = document.getElementById('dashboardSyncAction');
                    if (actionSpan) actionSpan.innerHTML = '';
                    self.loadDashboard();
                }
            }).catch(function () {});
        }, 500);
    },

    stopDashboardProgressPolling: function () {
        if (this.dashboardProgressInterval) {
            clearInterval(this.dashboardProgressInterval);
            this.dashboardProgressInterval = null;
        }
    },

    updateDashboardSyncButton: function (isRunning) {
        var btn = document.getElementById('btnDashboardSync');
        if (!btn) return;
        if (isRunning) {
            btn.querySelector('span').textContent = 'Cancel Sync';
            btn.style.background = '#c0392b';
        } else {
            btn.querySelector('span').textContent = 'Run Sync Now';
            btn.style.background = '';
        }
    },

    dashboardSync: function () {
        var self = this;
        var actionSpan = document.getElementById('dashboardSyncAction');

        // Check if sync is running (button shows "Cancel")
        var btn = document.getElementById('btnDashboardSync');
        if (btn && btn.querySelector('span').textContent === 'Cancel Sync') {
            if (actionSpan) actionSpan.innerHTML = '<span style="color: orange;">Cancelling...</span>';
            fetch(ApiClient.getUrl('XtreamLibrary/Cancel'), {
                method: 'POST',
                headers: { 'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken() }
            }).then(function () {
                // The polling will detect completion
            }).catch(function () {});
            return;
        }

        if (actionSpan) actionSpan.innerHTML = '<span style="color: orange;">Starting sync...</span>';
        self.updateDashboardSyncButton(true);

        fetch(ApiClient.getUrl('XtreamLibrary/Sync'), {
            method: 'POST',
            headers: { 'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken() }
        }).then(function (r) { return r.json(); }).then(function (data) {
            if (data.Success || (data.Message && data.Message.includes('already in progress'))) {
                if (actionSpan) actionSpan.innerHTML = '<span style="color: orange;">Sync in progress...</span>';
                self.startDashboardProgressPolling();
                // Also update the General tab sync button state
                self.isSyncing = true;
                var syncBtn = document.getElementById('btnManualSync');
                if (syncBtn) {
                    syncBtn.querySelector('span').textContent = 'Cancel Sync';
                    syncBtn.style.background = '#c0392b';
                }
                self.startProgressPolling();
                self.pollForCompletion();
            } else {
                self.updateDashboardSyncButton(false);
                if (actionSpan) actionSpan.innerHTML = '<span style="color: red;">' + (data.Message || 'Failed') + '</span>';
            }
        }).catch(function (err) {
            self.updateDashboardSyncButton(false);
            if (actionSpan) actionSpan.innerHTML = '<span style="color: red;">Failed: ' + (err.message || 'Error') + '</span>';
        });
    },

    dashboardRetryFailed: function () {
        var self = this;
        var actionSpan = document.getElementById('dashboardSyncAction');
        if (actionSpan) actionSpan.innerHTML = '<span style="color: orange;">Retrying failed items...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/RetryFailed'), {
            method: 'POST',
            headers: { 'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken() }
        }).then(function (r) { return r.json(); }).then(function (data) {
            if (data.Success) {
                if (actionSpan) actionSpan.innerHTML = '<span style="color: green;">Retry completed!</span>';
            } else {
                if (actionSpan) actionSpan.innerHTML = '<span style="color: red;">Retry failed: ' + (data.Error || 'Unknown') + '</span>';
            }
            self.loadDashboard();
            self.loadSyncStatus();
        }).catch(function (err) {
            if (actionSpan) actionSpan.innerHTML = '<span style="color: red;">Retry failed: ' + (err.message || 'Error') + '</span>';
        });
    },

    downloadLog: function () {
        var statusSpan = document.getElementById('downloadLogStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Preparing...</span>';

        fetch(ApiClient.getUrl('XtreamLibrary/DownloadLog'), {
            method: 'GET',
            headers: {
                'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken()
            }
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.blob();
        }).then(function (blob) {
            var url = URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = 'xtream-library-log.txt';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
            statusSpan.innerHTML = '<span style="color: green;">Downloaded!</span>';
        }).catch(function (error) {
            console.error('DownloadLog error:', error);
            statusSpan.innerHTML = '<span style="color: red;">Failed: ' + (error.message || 'Check console') + '</span>';
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

    var btnDashboardSync = document.getElementById('btnDashboardSync');
    if (btnDashboardSync) {
        btnDashboardSync.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.dashboardSync();
        });
    }

    var btnDashboardRetry = document.getElementById('btnDashboardRetry');
    if (btnDashboardRetry) {
        btnDashboardRetry.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.dashboardRetryFailed();
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

    var btnAdvancedToggle = document.getElementById('btnAdvancedToggle');
    if (btnAdvancedToggle) {
        btnAdvancedToggle.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.toggleAdvanced();
        });
    }

    var chkEnableDispatcharrMode = document.getElementById('chkEnableDispatcharrMode');
    if (chkEnableDispatcharrMode) {
        chkEnableDispatcharrMode.addEventListener('change', function () {
            XtreamLibraryConfig.updateDispatcharrVisibility();
        });
    }

    var btnTestDispatcharr = document.getElementById('btnTestDispatcharr');
    if (btnTestDispatcharr) {
        btnTestDispatcharr.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.testDispatcharr();
        });
    }

    var btnClearMetadataCache = document.getElementById('btnClearMetadataCache');
    if (btnClearMetadataCache) {
        btnClearMetadataCache.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.clearMetadataCache();
        });
    }

    var btnCleanMovies = document.getElementById('btnCleanMovies');
    if (btnCleanMovies) {
        btnCleanMovies.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.cleanMovies();
        });
    }

    var btnCleanSeries = document.getElementById('btnCleanSeries');
    if (btnCleanSeries) {
        btnCleanSeries.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.cleanSeries();
        });
    }

    var btnDownloadLog = document.getElementById('btnDownloadLog');
    if (btnDownloadLog) {
        btnDownloadLog.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.downloadLog();
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

    // Live channel mode radio — show/hide the custom selection UI on change.
    var liveModeRadios = document.getElementsByName('LiveChannelMode');
    for (var i = 0; i < liveModeRadios.length; i++) {
        liveModeRadios[i].addEventListener('change', function () {
            XtreamLibraryConfig.updateLiveChannelModeVisibility();
        });
    }

    // Live category search — instant client-side filter on the rendered list.
    var liveCategorySearch = document.getElementById('liveCategorySearch');
    if (liveCategorySearch) {
        liveCategorySearch.addEventListener('input', function () {
            XtreamLibraryConfig.filterLiveCategoryList();
        });
    }

    // Provider selector
    var selActiveProvider = document.getElementById('selActiveProvider');
    if (selActiveProvider) {
        selActiveProvider.addEventListener('change', function () {
            XtreamLibraryConfig.updateActiveProviderFromUI();
            var newIndex = parseInt(this.value);
            XtreamLibraryConfig.loadProviderIntoUI(newIndex);
        });
    }

    var btnAddProvider = document.getElementById('btnAddProvider');
    if (btnAddProvider) {
        btnAddProvider.addEventListener('click', function (e) {
            e.preventDefault();
            XtreamLibraryConfig.updateActiveProviderFromUI();
            var newIndex = XtreamLibraryConfig.providers.length;
            XtreamLibraryConfig.providers.push(XtreamLibraryConfig.makeDefaultProvider(newIndex));
            XtreamLibraryConfig.renderProviderSelector();
            XtreamLibraryConfig.loadProviderIntoUI(newIndex);
        });
    }

    var btnRemoveProvider = document.getElementById('btnRemoveProvider');
    if (btnRemoveProvider) {
        btnRemoveProvider.addEventListener('click', function (e) {
            e.preventDefault();
            if (XtreamLibraryConfig.providers.length <= 1) {
                Dashboard.alert('At least one provider is required.');
                return;
            }
            var idx = XtreamLibraryConfig.activeProviderIndex;
            var name = XtreamLibraryConfig.providers[idx].Name || ('Provider ' + (idx + 1));
            if (!confirm('Remove "' + name + '"? This cannot be undone.')) return;
            XtreamLibraryConfig.providers.splice(idx, 1);
            XtreamLibraryConfig.renderProviderSelector();
            XtreamLibraryConfig.loadProviderIntoUI(0);
        });
    }

    // Cleanup intervals on page unload
    window.addEventListener('beforeunload', function () {
        if (XtreamLibraryConfig.progressInterval) {
            clearInterval(XtreamLibraryConfig.progressInterval);
        }
        if (XtreamLibraryConfig.completionInterval) {
            clearInterval(XtreamLibraryConfig.completionInterval);
        }
        if (XtreamLibraryConfig.dashboardProgressInterval) {
            clearInterval(XtreamLibraryConfig.dashboardProgressInterval);
        }
    });

    XtreamLibraryConfig.loadConfig();
}

// Try multiple initialization methods for compatibility
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initXtreamLibraryConfig);
} else {
    initXtreamLibraryConfig();
}
