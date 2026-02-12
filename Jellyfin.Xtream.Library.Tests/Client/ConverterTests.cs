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

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Client;

public class ConverterTests
{
    #region StringBoolConverter Tests

    /// <summary>
    /// Test wrapper class that uses StringBoolConverter on a property.
    /// </summary>
    private class TestBoolWrapper
    {
        [JsonConverter(typeof(StringBoolConverter))]
        [JsonProperty("value")]
        public bool Value { get; set; }
    }

    [Fact]
    public void StringBoolConverter_String1_ReturnsTrue()
    {
        var json = "{\"value\": \"1\"}";

        var result = JsonConvert.DeserializeObject<TestBoolWrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().BeTrue();
    }

    [Fact]
    public void StringBoolConverter_String0_ReturnsFalse()
    {
        var json = "{\"value\": \"0\"}";

        var result = JsonConvert.DeserializeObject<TestBoolWrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().BeFalse();
    }

    [Fact]
    public void StringBoolConverter_OtherString_ReturnsFalse()
    {
        // Any string other than "1" should return false
        var json = "{\"value\": \"yes\"}";

        var result = JsonConvert.DeserializeObject<TestBoolWrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().BeFalse();
    }

    [Fact]
    public void StringBoolConverter_WriteTrue_Returns1()
    {
        var obj = new TestBoolWrapper { Value = true };

        var result = JsonConvert.SerializeObject(obj);

        result.Should().Contain("\"1\"");
    }

    [Fact]
    public void StringBoolConverter_WriteFalse_Returns0()
    {
        var obj = new TestBoolWrapper { Value = false };

        var result = JsonConvert.SerializeObject(obj);

        result.Should().Contain("\"0\"");
    }

    [Fact]
    public void StringBoolConverter_CanConvert_ReturnsTrueForString()
    {
        var converter = new StringBoolConverter();

        converter.CanConvert(typeof(string)).Should().BeTrue();
    }

    [Fact]
    public void StringBoolConverter_CanConvert_ReturnsFalseForInt()
    {
        var converter = new StringBoolConverter();

        converter.CanConvert(typeof(int)).Should().BeFalse();
    }

    [Fact]
    public void StringBoolConverter_NullValue_ReturnsFalse()
    {
        var json = "{\"value\": null}";

        var result = JsonConvert.DeserializeObject<TestBoolWrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().BeFalse();
    }

    [Fact]
    public void StringBoolConverter_Integer1_ReturnsTrue()
    {
        var json = "{\"value\": 1}";

        var result = JsonConvert.DeserializeObject<TestBoolWrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().BeTrue();
    }

    [Fact]
    public void StringBoolConverter_Integer0_ReturnsFalse()
    {
        var json = "{\"value\": 0}";

        var result = JsonConvert.DeserializeObject<TestBoolWrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().BeFalse();
    }

    [Fact]
    public void StringBoolConverter_BooleanTrue_ReturnsTrue()
    {
        var json = "{\"value\": true}";

        var result = JsonConvert.DeserializeObject<TestBoolWrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().BeTrue();
    }

    [Fact]
    public void StringBoolConverter_BooleanFalse_ReturnsFalse()
    {
        var json = "{\"value\": false}";

        var result = JsonConvert.DeserializeObject<TestBoolWrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().BeFalse();
    }

    #endregion

    #region Base64Converter Tests

    /// <summary>
    /// Test wrapper class that uses Base64Converter on a property.
    /// </summary>
    private class TestBase64Wrapper
    {
        [JsonConverter(typeof(Base64Converter))]
        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;
    }

    [Fact]
    public void Base64Converter_ValidBase64_DecodesCorrectly()
    {
        // "Hello" in base64 is "SGVsbG8="
        var json = "{\"value\": \"SGVsbG8=\"}";

        var result = JsonConvert.DeserializeObject<TestBase64Wrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().Be("Hello");
    }

    [Fact]
    public void Base64Converter_NullValue_ReturnsEmptyString()
    {
        var json = "{\"value\": null}";

        var result = JsonConvert.DeserializeObject<TestBase64Wrapper>(json);

        result.Should().NotBeNull();
        result!.Value.Should().BeEmpty();
    }

    #endregion

    #region OnlyObjectConverter Tests

    [Fact]
    public void OnlyObjectConverter_WithObject_DeserializesCorrectly()
    {
        var json = "{\"category_id\": 1, \"category_name\": \"Test\"}";

        var result = JsonConvert.DeserializeObject<Category>(json);

        result.Should().NotBeNull();
        result!.CategoryId.Should().Be(1);
        result.CategoryName.Should().Be("Test");
    }

    [Fact]
    public void OnlyObjectConverter_CanConvert_ReturnsCorrectType()
    {
        var converter = new OnlyObjectConverter<Category>();

        converter.CanConvert(typeof(Category)).Should().BeTrue();
        converter.CanConvert(typeof(string)).Should().BeFalse();
    }

    #endregion

    #region SingularToListConverter Tests

    [Fact]
    public void SingularToListConverter_WithArray_DeserializesAsList()
    {
        var json = "{\"backdrop_path\": [\"path1.jpg\", \"path2.jpg\"]}";

        var result = JsonConvert.DeserializeObject<Series>(json);

        result.Should().NotBeNull();
        result!.BackdropPaths.Should().HaveCount(2);
        result.BackdropPaths.Should().Contain("path1.jpg");
        result.BackdropPaths.Should().Contain("path2.jpg");
    }

    [Fact]
    public void SingularToListConverter_WithSingleString_DeserializesAsList()
    {
        var json = "{\"backdrop_path\": \"single.jpg\"}";

        var result = JsonConvert.DeserializeObject<Series>(json);

        result.Should().NotBeNull();
        result!.BackdropPaths.Should().HaveCount(1);
        result.BackdropPaths.Should().Contain("single.jpg");
    }

    [Fact]
    public void SingularToListConverter_WithNull_DeserializesAsEmptyList()
    {
        var json = "{\"backdrop_path\": null}";

        var result = JsonConvert.DeserializeObject<Series>(json);

        result.Should().NotBeNull();
        result!.BackdropPaths.Should().BeEmpty();
    }

    [Fact]
    public void SingularToListConverter_CanConvert_ReturnsCorrectType()
    {
        var converter = new SingularToListConverter<string>();

        converter.CanConvert(typeof(string)).Should().BeTrue();
        converter.CanConvert(typeof(int)).Should().BeFalse();
    }

    #endregion

    #region EpisodeDictionaryConverter Tests

    [Fact]
    public void EpisodeDictionaryConverter_NormalArrayEpisodes_DeserializesCorrectly()
    {
        var json = @"{
            ""seasons"": [],
            ""info"": {},
            ""episodes"": {
                ""1"": [
                    {""id"": 101, ""episode_num"": 1, ""title"": ""Pilot"", ""container_extension"": ""mp4"", ""season"": 1},
                    {""id"": 102, ""episode_num"": 2, ""title"": ""Second"", ""container_extension"": ""mp4"", ""season"": 1}
                ]
            }
        }";

        var result = JsonConvert.DeserializeObject<SeriesStreamInfo>(json);

        result.Should().NotBeNull();
        result!.Episodes.Should().NotBeNull();
        result.Episodes.Should().ContainKey(1);
        result.Episodes![1].Should().HaveCount(2);
    }

    [Fact]
    public void EpisodeDictionaryConverter_EmptyArray_ReturnsEmptyDict()
    {
        var json = @"{
            ""seasons"": [],
            ""info"": {},
            ""episodes"": []
        }";

        var result = JsonConvert.DeserializeObject<SeriesStreamInfo>(json);

        result.Should().NotBeNull();
        result!.Episodes.Should().NotBeNull();
        result.Episodes.Should().BeEmpty();
    }

    [Fact]
    public void EpisodeDictionaryConverter_Null_ReturnsEmptyDict()
    {
        var json = @"{
            ""seasons"": [],
            ""info"": {},
            ""episodes"": null
        }";

        var result = JsonConvert.DeserializeObject<SeriesStreamInfo>(json);

        result.Should().NotBeNull();
        result!.Episodes.Should().NotBeNull();
        result.Episodes.Should().BeEmpty();
    }

    [Fact]
    public void EpisodeDictionaryConverter_SingleEpisodeObject_WrapsInList()
    {
        var json = @"{
            ""seasons"": [],
            ""info"": {},
            ""episodes"": {
                ""1"": {""id"": 101, ""episode_num"": 1, ""title"": ""Only Episode"", ""container_extension"": ""mp4"", ""season"": 1}
            }
        }";

        var result = JsonConvert.DeserializeObject<SeriesStreamInfo>(json);

        result.Should().NotBeNull();
        result!.Episodes.Should().NotBeNull();
        result.Episodes.Should().ContainKey(1);
        result.Episodes![1].Should().HaveCount(1);
    }

    [Fact]
    public void EpisodeDictionaryConverter_MixedSeasons_HandlesBoth()
    {
        var json = @"{
            ""seasons"": [],
            ""info"": {},
            ""episodes"": {
                ""1"": [
                    {""id"": 101, ""episode_num"": 1, ""title"": ""S1E1"", ""container_extension"": ""mp4"", ""season"": 1},
                    {""id"": 102, ""episode_num"": 2, ""title"": ""S1E2"", ""container_extension"": ""mp4"", ""season"": 1}
                ],
                ""2"": {""id"": 201, ""episode_num"": 1, ""title"": ""S2E1"", ""container_extension"": ""mp4"", ""season"": 2}
            }
        }";

        var result = JsonConvert.DeserializeObject<SeriesStreamInfo>(json);

        result.Should().NotBeNull();
        result!.Episodes.Should().NotBeNull();
        result.Episodes.Should().ContainKey(1);
        result.Episodes.Should().ContainKey(2);
        result.Episodes![1].Should().HaveCount(2);
        result.Episodes[2].Should().HaveCount(1);
    }

    [Fact]
    public void EpisodeDictionaryConverter_CanConvert_ReturnsCorrectType()
    {
        var converter = new EpisodeDictionaryConverter();

        converter.CanConvert(typeof(Dictionary<int, ICollection<Episode>>)).Should().BeTrue();
        converter.CanConvert(typeof(string)).Should().BeFalse();
    }

    #endregion

    #region NullableEventHandler Tests

    [Fact]
    public void NullableEventHandler_ReleaseDateAsObject_GracefullyIgnored()
    {
        // Xtream APIs sometimes return "releasedate": {} instead of a string.
        // The error handler must suppress this for string? properties.
        var mockLogger = new Mock<ILogger<XtreamClient>>();
        var settings = new JsonSerializerSettings
        {
            Error = XtreamClient.NullableEventHandler(mockLogger.Object),
        };

        var json = @"{
            ""seasons"": [],
            ""info"": {},
            ""episodes"": {
                ""1"": [
                    {
                        ""id"": 101,
                        ""episode_num"": 1,
                        ""title"": ""Test"",
                        ""container_extension"": ""mp4"",
                        ""season"": 1,
                        ""info"": {
                            ""releasedate"": {},
                            ""plot"": ""A test episode"",
                            ""rating"": 7.5
                        }
                    }
                ]
            }
        }";

        var result = JsonConvert.DeserializeObject<SeriesStreamInfo>(json, settings);

        result.Should().NotBeNull();
        result!.Episodes.Should().ContainKey(1);
        result.Episodes![1].Should().HaveCount(1);
        var episode = result.Episodes[1].First();
        episode.Info.Should().NotBeNull();
        episode.Info!.ReleaseDate.Should().BeNull();
        episode.Info.Plot.Should().Be("A test episode");
        episode.Info.Rating.Should().Be(7.5m);
    }

    [Fact]
    public void NullableEventHandler_ReleaseDateAsString_ParsedNormally()
    {
        var mockLogger = new Mock<ILogger<XtreamClient>>();
        var settings = new JsonSerializerSettings
        {
            Error = XtreamClient.NullableEventHandler(mockLogger.Object),
        };

        var json = @"{
            ""seasons"": [],
            ""info"": {},
            ""episodes"": {
                ""1"": [
                    {
                        ""id"": 101,
                        ""episode_num"": 1,
                        ""title"": ""Test"",
                        ""container_extension"": ""mp4"",
                        ""season"": 1,
                        ""info"": {
                            ""releasedate"": ""2024-01-15"",
                            ""plot"": ""A test episode""
                        }
                    }
                ]
            }
        }";

        var result = JsonConvert.DeserializeObject<SeriesStreamInfo>(json, settings);

        result.Should().NotBeNull();
        var episode = result!.Episodes![1].First();
        episode.Info!.ReleaseDate.Should().Be("2024-01-15");
    }

    #endregion
}
