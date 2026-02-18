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
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    private readonly JsonSerializerSettings _serializerSettings = new()
    {
        Error = NullableEventHandler(logger),
    };

    private readonly object _userAgentLock = new();
    private volatile bool _userAgentConfigured;

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
        if (!_userAgentConfigured)
        {
            var config = Plugin.Instance?.Configuration;
            UpdateUserAgent(config?.UserAgent);
        }
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

            return JsonConvert.DeserializeObject<T>(jsonContent, _serializerSettings)!;
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
        int retryCount = 0;
        int currentDelay = RetryDelayMs;

        while (true)
        {
            try
            {
                using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                // Apply request delay to prevent rate limiting
                if (RequestDelayMs > 0)
                {
                    await Task.Delay(RequestDelayMs, cancellationToken).ConfigureAwait(false);
                }

                return content;
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
                await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                retryCount++;
                currentDelay *= 2; // Exponential backoff
            }
        }
    }

    public Task<PlayerApi> GetUserAndServerInfoAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        QueryApi<PlayerApi>(
          connectionInfo,
          $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}",
          cancellationToken);

    public Task<List<Category>> GetVodCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
         QueryApi<List<Category>>(
           connectionInfo,
           $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_vod_categories",
           cancellationToken);

    public Task<List<StreamInfo>> GetVodStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
         QueryApi<List<StreamInfo>>(
           connectionInfo,
           $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_vod_streams&category_id={categoryId}",
           cancellationToken);

    public Task<List<Category>> GetSeriesCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
         QueryApi<List<Category>>(
           connectionInfo,
           $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_series_categories",
           cancellationToken);

    public Task<List<Series>> GetSeriesByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
         QueryApi<List<Series>>(
           connectionInfo,
           $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_series&category_id={categoryId}",
           cancellationToken);

    public Task<SeriesStreamInfo> GetSeriesStreamsBySeriesAsync(ConnectionInfo connectionInfo, int seriesId, CancellationToken cancellationToken) =>
         QueryApi<SeriesStreamInfo>(
           connectionInfo,
           $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_series_info&series_id={seriesId}",
           cancellationToken);

    public async Task<VodInfoResponse?> GetVodInfoAsync(ConnectionInfo connectionInfo, int vodId, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryApi<VodInfoResponse>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_vod_info&vod_id={vodId}",
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
            $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_live_categories",
            cancellationToken);

    public Task<List<LiveStreamInfo>> GetLiveStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken) =>
        QueryApi<List<LiveStreamInfo>>(
            connectionInfo,
            $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_live_streams&category_id={categoryId}",
            cancellationToken);

    public Task<List<LiveStreamInfo>> GetAllLiveStreamsAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken) =>
        QueryApi<List<LiveStreamInfo>>(
            connectionInfo,
            $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_live_streams",
            cancellationToken);

    public async Task<EpgListings?> GetShortEpgAsync(ConnectionInfo connectionInfo, int streamId, int limit, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryApi<EpgListings>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_short_epg&stream_id={streamId}&limit={limit}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to fetch short EPG for stream ID {StreamId}", streamId);
            return null;
        }
    }

    public async Task<EpgListings?> GetSimpleDataTableAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryApi<EpgListings>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_simple_data_table&stream_id={streamId}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to fetch EPG data table for stream ID {StreamId}", streamId);
            return null;
        }
    }
}
