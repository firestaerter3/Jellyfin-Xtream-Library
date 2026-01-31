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
using System.Collections.Generic;
using System.Linq;
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
/// </remarks>
/// <param name="client">The HTTP client used.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public class XtreamClient(HttpClient client, ILogger<XtreamClient> logger) : IDisposable, IXtreamClient
{
    private readonly JsonSerializerSettings _serializerSettings = new()
    {
        Error = NullableEventHandler(logger),
    };

    /// <summary>
    /// Updates the User-Agent header based on plugin configuration.
    /// </summary>
    /// <param name="customUserAgent">Optional custom user agent string.</param>
    public void UpdateUserAgent(string? customUserAgent = null)
    {
        client.DefaultRequestHeaders.UserAgent.Clear();
        if (string.IsNullOrWhiteSpace(customUserAgent))
        {
            ProductHeaderValue header = new ProductHeaderValue("Jellyfin.Xtream.Library", Assembly.GetExecutingAssembly().GetName().Version?.ToString());
            ProductInfoHeaderValue userAgent = new ProductInfoHeaderValue(header);
            client.DefaultRequestHeaders.UserAgent.Add(userAgent);
        }
        else
        {
            client.DefaultRequestHeaders.Add("User-Agent", customUserAgent);
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
                PropertyInfo? property = type.GetProperties().FirstOrDefault((p) =>
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

                if (property != null && Nullable.GetUnderlyingType(property.PropertyType) != null)
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
        string jsonContent = await client.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);

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
        catch (JsonSerializationException ex)
        {
            string jsonSample = jsonContent.Length > 500 ? string.Concat(jsonContent.AsSpan(0, 500), "...") : jsonContent;
            logger.LogError(ex, "Failed to deserialize response from Xtream API (URL: {Url}). JSON content: {Json}", uri, jsonSample);
            throw;
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

    /// <summary>
    /// Dispose the HTTP client.
    /// </summary>
    /// <param name="b">Unused.</param>
    protected virtual void Dispose(bool b)
    {
        client?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
