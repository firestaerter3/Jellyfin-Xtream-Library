// CS0618: Legacy PluginConfiguration fields still used here; Phase 4/5 migrates to ProviderConfig.
#pragma warning disable CS0618

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
using System.Linq;
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
        // Trimmed for the same reason as ConnectionInfo: whitespace saved with the credential
        // otherwise reaches the JWT request and the login fails with no useful message.
        username = (username ?? string.Empty).Trim();
        password = (password ?? string.Empty).Trim();

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
            var json = await GetAuthenticatedAsync(baseUrl, $"{baseUrl}/api/vod/movies/{movieId}/", cancellationToken).ConfigureAwait(false);
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
            var json = await GetAuthenticatedAsync(baseUrl, $"{baseUrl}/api/vod/movies/{movieId}/providers/", cancellationToken).ConfigureAwait(false);
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
    public async Task<VodInfoResponse?> GetMovieProviderInfoAsync(string baseUrl, int movieId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await GetAuthenticatedAsync(baseUrl, $"{baseUrl}/api/vod/movies/{movieId}/provider-info/", cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return null;
            }

            var dto = JsonConvert.DeserializeObject<DispatcharrMovieProviderInfoDto>(json);
            if (dto == null)
            {
                return null;
            }

            return new VodInfoResponse
            {
                Info = new VodInfoDetails
                {
                    Name = dto.Name,
                    OriginalName = dto.OName,
                    Plot = !string.IsNullOrEmpty(dto.Plot) ? dto.Plot : dto.Description,
                    Cast = dto.Actors,
                    Director = dto.Director,
                    Genre = dto.Genre,
                    Country = dto.Country,
                    ReleaseDate = dto.ReleaseDate,
                    Rating = dto.Rating,
                    TmdbId = dto.TmdbId,
                    YoutubeTrailer = dto.YoutubeTrailer,
                    BackdropPaths = dto.BackdropPath ?? new List<string>(),
                    DurationSecs = dto.DurationSecs,
                    Bitrate = dto.Bitrate,
                    Video = dto.Video,
                    Audio = dto.Audio,
                },
                MovieData = new VodMovieData
                {
                    StreamId = int.TryParse(dto.StreamId, out var streamId) ? streamId : movieId,
                    Name = dto.Name ?? string.Empty,
                    ContainerExtension = dto.ContainerExtension ?? string.Empty,
                },
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get Dispatcharr provider info for movie {MovieId}", movieId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SeriesInfo?> GetSeriesProviderInfoAsync(string baseUrl, int seriesId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await GetAuthenticatedAsync(baseUrl, $"{baseUrl}/api/vod/series/{seriesId}/provider-info/", cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return null;
            }

            var dto = JsonConvert.DeserializeObject<DispatcharrSeriesProviderInfoDto>(json);
            if (dto == null)
            {
                return null;
            }

            return new SeriesInfo
            {
                Name = dto.Name ?? string.Empty,
                Plot = dto.Description ?? string.Empty,
                Genre = dto.Genre ?? string.Empty,
                Rating = decimal.TryParse(dto.Rating, out var rating) ? rating : 0,
                BackdropPaths = dto.BackdropPath ?? new List<string>(),
                Tmdb = dto.TmdbId,
                Imdb = dto.ImdbId,
                CategoryId = dto.CategoryId ?? 0,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get Dispatcharr provider info for series {SeriesId}", seriesId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<int, ICollection<Episode>>> GetSeriesEpisodesAsync(string baseUrl, int seriesId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, ICollection<Episode>>();
        try
        {
            var json = await GetAuthenticatedAsync(baseUrl, $"{baseUrl}/api/vod/series/{seriesId}/episodes/", cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return result;
            }

            var episodes = JsonConvert.DeserializeObject<List<DispatcharrEpisodeDto>>(json);
            if (episodes == null)
            {
                return result;
            }

            foreach (var ep in episodes)
            {
                if (ep.Providers == null || ep.Providers.Count == 0 || ep.SeasonNumber == null)
                {
                    continue;
                }

                // Same "highest account priority wins" rule Dispatcharr itself uses server-side
                // when multiple providers carry the same episode.
                var best = ep.Providers.OrderByDescending(p => p.M3uAccount?.Priority ?? 0).First();
                if (!int.TryParse(best.StreamId, out var episodeStreamId))
                {
                    continue;
                }

                if (!result.TryGetValue(ep.SeasonNumber.Value, out var seasonEpisodes))
                {
                    seasonEpisodes = new List<Episode>();
                    result[ep.SeasonNumber.Value] = seasonEpisodes;
                }

                seasonEpisodes.Add(new Episode
                {
                    EpisodeId = episodeStreamId,
                    EpisodeNum = ep.EpisodeNumber ?? 0,
                    Title = ep.Name ?? string.Empty,
                    ContainerExtension = best.ContainerExtension ?? "mkv",
                    Season = ep.SeasonNumber.Value,
                });
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get Dispatcharr episodes for series {SeriesId}", seriesId);
            return result;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DispatcharrChannel>> GetChannelsAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            // include_streams gives each channel the upstream streams behind it, which is what
            // carries the stream id this plugin knows the channel by.
            //
            // No paging parameter on purpose. Dispatcharr returns a bare JSON array unless "page"
            // or "page_size" is present, and sending either turns the response into
            // {count, results} and breaks this deserialize (GitHub #113).
            var json = await GetAuthenticatedAsync(
                baseUrl,
                $"{baseUrl}/api/channels/channels/?include_streams=true",
                cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return Array.Empty<DispatcharrChannel>();
            }

            return JsonConvert.DeserializeObject<List<DispatcharrChannel>>(json)
                   ?? (IReadOnlyList<DispatcharrChannel>)Array.Empty<DispatcharrChannel>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Information rather than Debug: every channel silently falling back to a URL with the
            // password in it is exactly the outcome this is meant to prevent, so it should not be
            // invisible.
            _logger.LogInformation(ex, "Could not read Dispatcharr's channel list; Live TV URLs will carry credentials");
            return Array.Empty<DispatcharrChannel>();
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

    private async Task<string?> GetAuthenticatedAsync(string baseUrl, string url, CancellationToken cancellationToken)
    {
        // The base URL is passed in rather than derived from the request URL. Deriving it as
        // scheme://authority dropped any path, so a Dispatcharr behind a reverse proxy on a subpath
        // had its data calls sent to the right place and its login sent to the wrong one (#83).
        await EnsureTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false);

        if (_accessToken == null)
        {
            return null;
        }

        int retryCount = 0;
        int maxRetries = 3;
        int currentDelay = 1000;
        try
        {
            var pluginConfig = Plugin.Instance.Configuration;
            maxRetries = pluginConfig.MaxRetries;
            currentDelay = pluginConfig.RetryDelayMs;
        }
        catch (Exception)
        {
            // Plugin not initialized (e.g. in tests) — use defaults
        }

        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

                await RequestGate.WaitTurnAsync(request.RequestUri!, RequestDelayMs, cancellationToken).ConfigureAwait(false);
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
                    await RequestGate.WaitTurnAsync(retryRequest.RequestUri!, RequestDelayMs, cancellationToken).ConfigureAwait(false);
                    using var retryResponse = await _httpClient.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
                    retryResponse.EnsureSuccessStatusCode();
                    return await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

                // Same as the Xtream client: the whole host backs off, not just this request.
                RequestGate.Pause(new Uri(url), currentDelay);
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
            using var response = await _httpClient.PostAsync($"{baseUrl}/api/accounts/token/", content, cancellationToken).ConfigureAwait(false);

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
                _tokenExpiry = DateTime.UtcNow.AddMinutes(25); // Tokens last 30 min, refresh early
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
            using var response = await _httpClient.PostAsync($"{baseUrl}/api/accounts/token/refresh/", content, cancellationToken).ConfigureAwait(false);

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
