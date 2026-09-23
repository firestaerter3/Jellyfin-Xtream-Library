// CS0618: Legacy PluginConfiguration fields still used here; Phase 4 migrates to ProviderConfig.
#pragma warning disable CS0618

// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// The Xtream API client implementation.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamClient"/> class.
/// Note: HttpClient is managed by IHttpClientFactory - do not dispose manually.
/// </remarks>
/// <param name="client">The HTTP client used (managed by IHttpClientFactory).</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public class XtreamClient(HttpClient client, ILogger<XtreamClient> logger) : IXtreamClient
{
    /// <summary>
    /// Ceiling for the doubled retry delay. Without it, a large configured delay overflows the
    /// int handed to Task.Delay after a few retries.
    /// </summary>
    private const int MaxRetryDelayMs = 60000;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    private readonly JsonSerializerSettings _serializerSettings = new()
    {
        Error = NullableEventHandler(logger),
    };

    private readonly object _userAgentLock = new();
    private readonly object _timeoutLock = new();
    private volatile bool _userAgentConfigured;
    private volatile bool _httpTimeoutDisabled;

    /// <summary>
    /// Gets or sets the per-request timeout for API calls. A single call against a provider
    /// with a very large catalog can take minutes, so this is configurable per provider via
    /// <c>ProviderConfig.TimeoutSeconds</c>.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the delay in milliseconds between API requests.
    /// </summary>
    public int RequestDelayMs { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of retries for rate-limited requests.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial retry delay in milliseconds after a 429 response.
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Updates the User-Agent header based on plugin configuration.
    /// </summary>
    /// <param name="customUserAgent">Optional custom user agent string.</param>
    public void UpdateUserAgent(string? customUserAgent = null)
    {
        lock (_userAgentLock)
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            if (string.IsNullOrWhiteSpace(customUserAgent))
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
                client.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue(new ProductHeaderValue("Jellyfin.Xtream.Library", version)));
            }
            else
            {
                // Use typed API only — mixing Add("User-Agent", ...) with UserAgent collection corrupts headers
                if (ProductInfoHeaderValue.TryParse(customUserAgent, out var parsed))
                {
                    client.DefaultRequestHeaders.UserAgent.Add(parsed);
                }
                else
                {
                    // Wrap as comment token for non-standard user agent strings
                    client.DefaultRequestHeaders.UserAgent.Add(
                        new ProductInfoHeaderValue($"({customUserAgent})"));
                }
            }

            _userAgentConfigured = true;
        }
    }

    private void EnsureUserAgent()
    {
        if (_userAgentConfigured)
        {
            return;
        }

        // Set User-Agent before any requests are in flight
        var config = Plugin.Instance?.Configuration;
        UpdateUserAgent(config?.UserAgent);
    }

    /// <summary>
    /// Hands timeout control to <see cref="Timeout"/> by removing the HttpClient's own ceiling.
    /// Without this the IHttpClientFactory default of 100 seconds silently wins over any larger
    /// configured value. Must run before the first request: HttpClient.Timeout throws once a
    /// request has been sent, which is why this is separate from the User-Agent setup (that one
    /// can be re-run at any time via UpdateUserAgent).
    /// </summary>
    private void EnsureHttpTimeoutDisabled()
    {
        if (_httpTimeoutDisabled)
        {
            return;
        }

        lock (_timeoutLock)
        {
            if (_httpTimeoutDisabled)
            {
                return;
            }

            try
            {
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            }
            catch (InvalidOperationException)
            {
                // A request already went out on this client, so its ceiling is fixed for the
                // client's lifetime. Our own timeout still applies, it just cannot exceed it.
                logger.LogDebug(
                    "Could not lift the HttpClient timeout; effective ceiling stays {Ceiling}",
                    client.Timeout);
            }

            _httpTimeoutDisabled = true;
        }
    }

    private static string EncodeCredentials(ConnectionInfo connectionInfo)
    {
        if (string.IsNullOrWhiteSpace(connectionInfo.UserName) &&
            string.IsNullOrWhiteSpace(connectionInfo.Password))
        {
            return string.Empty;
        }

        return $"username={Uri.EscapeDataString(connectionInfo.UserName)}&password={Uri.EscapeDataString(connectionInfo.Password)}";
    }

    private static string PlayerApiUrl(ConnectionInfo connectionInfo, string? queryPart = null)
    {
        var creds = EncodeCredentials(connectionInfo);
        if (string.IsNullOrEmpty(queryPart))
        {
            return string.IsNullOrEmpty(creds) ? "/player_api.php" : $"/player_api.php?{creds}";
        }

        return string.IsNullOrEmpty(creds)
            ? $"/player_api.php?{queryPart}"
            : $"/player_api.php?{creds}&{queryPart}";
    }

    /// <summary>
    /// Ignores error events if the target property is nullable.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <returns>An event handler using the given logger.</returns>
    public static EventHandler<ErrorEventArgs> NullableEventHandler(ILogger<XtreamClient> logger)
    {
        return (object? sender, ErrorEventArgs args) =>
        {
            if (args.ErrorContext.OriginalObject?.GetType() is Type type && args.ErrorContext.Member is string jsonName)
            {
                PropertyInfo[] properties = PropertyCache.GetOrAdd(type, t => t.GetProperties());
                PropertyInfo? property = properties.FirstOrDefault((p) =>
                {
                    CustomAttributeData? attribute = p.CustomAttributes.FirstOrDefault(a => a.AttributeType == typeof(JsonPropertyAttribute));
                    if (attribute == null)
                    {
                        return false;
                    }

                    if (attribute.ConstructorArguments.Count > 0)
                    {
                        string? value = attribute.ConstructorArguments.First().Value as string;
                        return jsonName.Equals(value, StringComparison.Ordinal);
                    }
                    else
                    {
                        return jsonName.Equals(p.Name, StringComparison.Ordinal);
                    }
                });

                if (property != null && (!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) != null))
                {
                    logger.LogDebug("Property `{PropertyName}` (`{JsonName}` in JSON) is nullable, ignoring parsing error!", property.Name, jsonName);
                    args.ErrorContext.Handled = true;
                }
            }
        };
    }

    private async Task<T> QueryApi<T>(ConnectionInfo connectionInfo, string urlPath, CancellationToken cancellationToken)
    {
        Uri uri = new Uri(connectionInfo.BaseUrl + urlPath);
        string jsonContent = await GetStringWithRetryAsync(uri, cancellationToken).ConfigureAwait(false);

        try
        {
            string trimmedJson = jsonContent.TrimStart();
            if (trimmedJson.StartsWith('[') && typeof(T) == typeof(SeriesStreamInfo))
            {
                logger.LogWarning("Xtream API returned array instead of object for SeriesStreamInfo (URL: {Url}). Returning empty object.", uri);
                return (T)(object)new SeriesStreamInfo();
            }

            return JsonConvert.DeserializeObject<T>(jsonContent, _serializerSettings)
                ?? throw new JsonException($"Xtream API returned null for {typeof(T).Name} from {uri}");
        }
        catch (JsonException ex)
        {
            string jsonSample = jsonContent.Length > 500 ? string.Concat(jsonContent.AsSpan(0, 500), "...") : jsonContent;
            logger.LogError(ex, "Failed to deserialize response from Xtream API (URL: {Url}). JSON content: {Json}", uri, jsonSample);
            throw;
        }
    }

    private async Task<string> GetStringWithRetryAsync(Uri uri, CancellationToken cancellationToken)
    {
        EnsureUserAgent();
        EnsureHttpTimeoutDisabled();
        int retryCount = 0;
        int currentDelay = RetryDelayMs;

        while (true)
        {
            // Before the timeout starts: time spent waiting for this host's turn is not time the
            // provider took to answer (GitHub #122).
            await RequestGate.WaitTurnAsync(uri, RequestDelayMs, cancellationToken).ConfigureAwait(false);

            // Bounds this attempt without touching HttpClient.Timeout, which is immutable
            // once a request has been sent and would therefore not be reconfigurable per
            // provider on the long-lived client.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);

            try
            {
                using var response = await client.GetAsync(uri, timeoutCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests ||
                (ex.StatusCode.HasValue && (int)ex.StatusCode.Value >= 500))
            {
                if (retryCount >= MaxRetries)
                {
                    logger.LogError("HTTP {StatusCode} after {Retries} retries for URL: {Url}", (int?)ex.StatusCode, retryCount, uri);
                    throw;
                }

                logger.LogWarning("HTTP {StatusCode} for URL: {Url}. Retry {Retry}/{MaxRetries} after {Delay}ms", (int?)ex.StatusCode, uri, retryCount + 1, MaxRetries, currentDelay);

                // Every request to this host waits out the backoff, not only this one; the retry
                // below takes its turn after it like everything else.
                RequestGate.Pause(uri, currentDelay);
                retryCount++;
                currentDelay = Math.Min(currentDelay * 2, MaxRetryDelayMs); // Exponential backoff, capped
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // Our own timeout token fired, rather than the caller cancelling or a
                // cancellation arriving from the transport. Checking the timeout token matters:
                // without it any OperationCanceledException got retried and then relabelled as
                // a provider timeout, pointing diagnosis at the wrong thing. Previously this
                // escaped as a bare TaskCanceledException and was never retried, so a single
                // slow catalog response failed the whole channel fetch.
                if (retryCount >= MaxRetries)
                {
                    logger.LogError(
                        "Timed out after {Timeout} and {Retries} retries for URL: {Url}",
                        Timeout,
                        retryCount,
                        uri);

                    // Message deliberately carries no query string: Xtream URLs embed the
                    // provider username and password.
                    throw new TimeoutException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Xtream request to {uri.Host} timed out after {Timeout.TotalSeconds:F0}s ({retryCount} retries). Raise the provider timeout if the catalog is very large."));
                }

                logger.LogWarning(
                    "Timed out after {Timeout} for URL: {Url}. Retry {Retry}/{MaxRetries} after {Delay}ms",
                    Timeout,
                    uri,
                    retryCount + 1,
                    MaxRetries,
                    currentDelay);
                await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                retryCount++;
                currentDelay = Math.Min(currentDelay * 2, MaxRetryDelayMs); // Exponential backoff, capped
            }
        }
    }

    public Task<PlayerApi> GetUserAndServerInfoAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        QueryApi<PlayerApi>(
          connectionInfo,
          PlayerApiUrl(connectionInfo),
          cancellationToken);

    public Task<List<Category>> GetVodCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
         QueryApi<List<Category>>(
           connectionInfo,
           PlayerApiUrl(connectionInfo, "action=get_vod_categories"),
           cancellationToken);

    public Task<List<StreamInfo>> GetVodStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
         QueryApi<List<StreamInfo>>(
           connectionInfo,
           PlayerApiUrl(connectionInfo, $"action=get_vod_streams&category_id={categoryId}"),
           cancellationToken);

    public Task<List<Category>> GetSeriesCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
         QueryApi<List<Category>>(
           connectionInfo,
           PlayerApiUrl(connectionInfo, "action=get_series_categories"),
           cancellationToken);

    public Task<List<Series>> GetSeriesByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
         QueryApi<List<Series>>(
           connectionInfo,
           PlayerApiUrl(connectionInfo, $"action=get_series&category_id={categoryId}"),
           cancellationToken);

    public Task<SeriesStreamInfo> GetSeriesStreamsBySeriesAsync(ConnectionInfo connectionInfo, int seriesId, CancellationToken cancellationToken) =>
         QueryApi<SeriesStreamInfo>(
           connectionInfo,
           PlayerApiUrl(connectionInfo, $"action=get_series_info&series_id={seriesId}"),
           cancellationToken);

    public async Task<VodInfoResponse?> GetVodInfoAsync(ConnectionInfo connectionInfo, int vodId, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryApi<VodInfoResponse>(
                connectionInfo,
                PlayerApiUrl(connectionInfo, $"action=get_vod_info&vod_id={vodId}"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to fetch VOD info for ID {VodId}", vodId);
            return null;
        }
    }

    public Task<List<Category>> GetLiveCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        QueryApi<List<Category>>(
            connectionInfo,
            PlayerApiUrl(connectionInfo, "action=get_live_categories"),
            cancellationToken);

    public Task<List<LiveStreamInfo>> GetLiveStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
        QueryApi<List<LiveStreamInfo>>(
            connectionInfo,
            PlayerApiUrl(connectionInfo, $"action=get_live_streams&category_id={categoryId}"),
            cancellationToken);

    public Task<List<LiveStreamInfo>> GetAllLiveStreamsAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        QueryApi<List<LiveStreamInfo>>(
            connectionInfo,
            PlayerApiUrl(connectionInfo, "action=get_live_streams"),
            cancellationToken);

    public async Task<EpgListings?> GetSimpleDataTableAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryApi<EpgListings>(
                connectionInfo,
                PlayerApiUrl(connectionInfo, $"action=get_simple_data_table&stream_id={streamId}"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to fetch EPG data table for stream ID {StreamId}", streamId);
            return null;
        }
    }

    public async Task<string?> GetXmltvAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken)
    {
        try
        {
            var creds = EncodeCredentials(connectionInfo);
            var path = string.IsNullOrEmpty(creds) ? "/xmltv.php" : $"/xmltv.php?{creds}";
            var uri = new Uri(connectionInfo.BaseUrl + path);
            return await GetStringWithRetryAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch upstream XMLTV from {BaseUrl}", connectionInfo.BaseUrl);
            return null;
        }
    }
}
