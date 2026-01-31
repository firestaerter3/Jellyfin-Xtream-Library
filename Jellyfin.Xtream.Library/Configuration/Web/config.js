const XtreamLibraryConfig = {
    pluginUniqueId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',

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

            ApiClient.updatePluginConfiguration(XtreamLibraryConfig.pluginUniqueId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    },

    testConnection: function () {
        const statusSpan = document.getElementById('connectionStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Testing...</span>';

        ApiClient.fetch({
            url: ApiClient.getUrl('XtreamLibrary/TestConnection'),
            type: 'GET'
        }).then(function (response) {
            if (response.Success) {
                statusSpan.innerHTML = '<span style="color: green;">' + response.Message + '</span>';
            } else {
                statusSpan.innerHTML = '<span style="color: red;">' + response.Message + '</span>';
            }
        }).catch(function (error) {
            statusSpan.innerHTML = '<span style="color: red;">Connection failed: ' + (error.message || 'Unknown error') + '</span>';
        });
    },

    runSync: function () {
        const statusSpan = document.getElementById('syncStatus');
        statusSpan.innerHTML = '<span style="color: orange;">Syncing...</span>';

        ApiClient.fetch({
            url: ApiClient.getUrl('XtreamLibrary/Sync'),
            type: 'POST'
        }).then(function (response) {
            if (response.Success) {
                statusSpan.innerHTML = '<span style="color: green;">Sync completed!</span>';
                XtreamLibraryConfig.displaySyncResult(response);
            } else {
                statusSpan.innerHTML = '<span style="color: red;">Sync failed: ' + (response.Error || 'Unknown error') + '</span>';
            }
        }).catch(function (error) {
            statusSpan.innerHTML = '<span style="color: red;">Sync failed: ' + (error.message || 'Unknown error') + '</span>';
        });
    },

    loadSyncStatus: function () {
        ApiClient.fetch({
            url: ApiClient.getUrl('XtreamLibrary/Status'),
            type: 'GET'
        }).then(function (response) {
            if (response) {
                XtreamLibraryConfig.displaySyncResult(response);
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
    }
};

document.getElementById('XtreamLibraryConfigForm').addEventListener('submit', function (e) {
    e.preventDefault();
    XtreamLibraryConfig.saveConfig();
    return false;
});

document.getElementById('btnTestConnection').addEventListener('click', function () {
    XtreamLibraryConfig.testConnection();
});

document.getElementById('btnManualSync').addEventListener('click', function () {
    XtreamLibraryConfig.runSync();
});

XtreamLibraryConfig.loadConfig();
