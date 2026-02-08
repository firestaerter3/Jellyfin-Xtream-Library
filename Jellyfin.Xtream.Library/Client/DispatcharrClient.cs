// Copyright (C) 2024  Roland Breitschaft

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
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// HTTP client for Dispatcharr's REST API with JWT authentication.
/// </summary>
public class DispatcharrClient : IDispatcharrClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DispatcharrClient> _logger;

    private string _username = string.Empty;
    private string _password = string.Empty;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatcharrClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client (managed by IHttpClientFactory).</param>
    /// <param name="logger">The logger instance.</param>
    public DispatcharrClient(HttpClient httpClient, ILogger<DispatcharrClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public int RequestDelayMs { get; set; }

    /// <inheritdoc />
    public void Configure(string username, string password)
    {
        if (!string.Equals(_username, username, StringComparison.Ordinal) ||
            !string.Equals(_password, password, StringComparison.Ordinal))
        {
            _username = username;
            _password = password;
            _accessToken = null;
            _refreshToken = null;
            _tokenExpiry = DateTime.MinValue;
        }
    }

    /// <inheritdoc />
    public async Task<DispatcharrMovieDetail?> GetMovieDetailAsync(string baseUrl, int movieId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await GetAuthenticatedAsync($"{baseUrl}/api/vod/movies/{movieId}/", cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return null;
            }

            return JsonConvert.DeserializeObject<DispatcharrMovieDetail>(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get Dispatcharr movie detail for ID {MovieId}", movieId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<List<DispatcharrMovieProvider>> GetMovieProvidersAsync(string baseUrl, int movieId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await GetAuthenticatedAsync($"{baseUrl}/api/vod/movies/{movieId}/providers/", cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return new List<DispatcharrMovieProvider>();
            }

            return JsonConvert.DeserializeObject<List<DispatcharrMovieProvider>>(json) ?? new List<DispatcharrMovieProvider>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get Dispatcharr movie providers for ID {MovieId}", movieId);
            return new List<DispatcharrMovieProvider>();
        }
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectionAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var token = await LoginAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            return token != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dispatcharr connection test failed");
            return false;
        }
    }

    private async Task<string?> GetAuthenticatedAsync(string url, CancellationToken cancellationToken)
    {
        // Extract base URL from the full URL for token acquisition
        var uri = new Uri(url);
        var baseUrl = $"{uri.Scheme}://{uri.Authority}";

        await EnsureTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false);

        if (_accessToken == null)
        {
            return null;
        }

        int retryCount = 0;
        int maxRetries = 3;
        int currentDelay = 1000;

        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Token expired, try refresh
                    _logger.LogDebug("Dispatcharr token expired, refreshing...");
                    var refreshed = await RefreshTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                    if (!refreshed)
                    {
                        // Full re-login
                        var token = await LoginAsync(baseUrl, cancellationToken).ConfigureAwait(false);
                        if (token == null)
                        {
                            return null;
                        }
                    }

                    // Retry with new token
                    using var retryRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                    using var retryResponse = await _httpClient.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
                    retryResponse.EnsureSuccessStatusCode();
                    var retryContent = await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    if (RequestDelayMs > 0)
                    {
                        await Task.Delay(RequestDelayMs, cancellationToken).ConfigureAwait(false);
                    }

                    return retryContent;
                }

                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (RequestDelayMs > 0)
                {
                    await Task.Delay(RequestDelayMs, cancellationToken).ConfigureAwait(false);
                }

                return content;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests ||
                (ex.StatusCode.HasValue && (int)ex.StatusCode.Value >= 500))
            {
                if (retryCount >= maxRetries)
                {
                    _logger.LogError("HTTP {StatusCode} after {Retries} retries for URL: {Url}", (int?)ex.StatusCode, retryCount, url);
                    throw;
                }

                _logger.LogWarning("HTTP {StatusCode} for URL: {Url}. Retry {Retry}/{MaxRetries} after {Delay}ms", (int?)ex.StatusCode, url, retryCount + 1, maxRetries, currentDelay);
                await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                retryCount++;
                currentDelay *= 2;
            }
        }
    }

    private async Task EnsureTokenAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry)
        {
            return;
        }

        // Try refresh first if we have a refresh token
        if (_refreshToken != null)
        {
            var refreshed = await RefreshTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            if (refreshed)
            {
                return;
            }
        }

        // Full login
        await LoginAsync(baseUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DispatcharrTokenResponse?> LoginAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonConvert.SerializeObject(new { username = _username, password = _password });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{baseUrl}/api/token/", content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Dispatcharr JWT login failed with status {StatusCode}", response.StatusCode);
                _accessToken = null;
                _refreshToken = null;
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var tokenResponse = JsonConvert.DeserializeObject<DispatcharrTokenResponse>(json);

            if (tokenResponse != null && !string.IsNullOrEmpty(tokenResponse.Access))
            {
                _accessToken = tokenResponse.Access;
                _refreshToken = tokenResponse.Refresh;
                _tokenExpiry = DateTime.UtcNow.AddMinutes(4); // Tokens last ~5 min, refresh early
                _logger.LogDebug("Dispatcharr JWT login successful");
            }

            return tokenResponse;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dispatcharr JWT login failed");
            _accessToken = null;
            _refreshToken = null;
            return null;
        }
    }

    private async Task<bool> RefreshTokenAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (_refreshToken == null)
        {
            return false;
        }

        try
        {
            var payload = JsonConvert.SerializeObject(new { refresh = _refreshToken });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{baseUrl}/api/token/refresh/", content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Dispatcharr token refresh failed with status {StatusCode}", response.StatusCode);
                _refreshToken = null;
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var tokenResponse = JsonConvert.DeserializeObject<DispatcharrTokenResponse>(json);

            if (tokenResponse != null && !string.IsNullOrEmpty(tokenResponse.Access))
            {
                _accessToken = tokenResponse.Access;
                _tokenExpiry = DateTime.UtcNow.AddMinutes(4);
                _logger.LogDebug("Dispatcharr token refreshed successfully");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dispatcharr token refresh failed");
            _refreshToken = null;
            return false;
        }
    }
}
