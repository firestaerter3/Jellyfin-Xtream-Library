using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Service;

internal sealed class ArtworkFailureTracker
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Func<int> _failureThreshold;
    private readonly Func<int> _blacklistDays;
    private readonly Dictionary<string, Entry> _states = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private long _journalLines;

    public ArtworkFailureTracker(string dataPath, ILogger logger, Func<int>? threshold = null, Func<int>? days = null)
    {
        _path = Path.Combine(dataPath, "xtream-library", "artwork_failures.jsonl");
        _logger = logger;
        _failureThreshold = threshold ?? (() => 3);
        _blacklistDays = days ?? (() => 7);
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            foreach (var line in File.ReadLines(_path))
            {
                _journalLines++;
                try
                {
                    var entry = JsonConvert.DeserializeObject<Entry>(line);
                    if (entry is null || string.IsNullOrEmpty(entry.Hash) || entry.Hash.Length != 64)
                    {
                        continue;
                    }

                    if (entry.Failures == 0 || entry.RetryAfterUtc <= DateTime.UtcNow)
                    {
                        _states.Remove(entry.Hash);
                    }
                    else
                    {
                        _states[entry.Hash] = entry;
                    }
                }
                catch (JsonException)
                {
                    // Ignore incomplete journal entries.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load artwork failure journal");
        }
    }

    public static string Hash(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

    public SemaphoreSlim Gate(string hash) => _gates[Convert.ToInt32(hash[..2], 16) % _gates.Length];

    public bool IsBlocked(string hash)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(hash, out var entry))
            {
                return false;
            }

            if (entry.RetryAfterUtc <= DateTime.UtcNow)
            {
                _states.Remove(hash);
                return false;
            }

            return entry.Failures >= Math.Clamp(_failureThreshold(), 1, 10);
        }
    }

    public void Record(string hash, bool success)
    {
        lock (_sync)
        {
            if (success && !_states.ContainsKey(hash))
            {
                return;
            }

            Entry entry;
            if (success)
            {
                _states.Remove(hash);
                entry = new Entry { Hash = hash };
            }
            else
            {
                if (!_states.TryGetValue(hash, out var existing) || existing.RetryAfterUtc <= DateTime.UtcNow)
                {
                    existing = new Entry { Hash = hash };
                }

                entry = existing;
                entry.Failures = Math.Min(Math.Clamp(_failureThreshold(), 1, 10), entry.Failures + 1);
                entry.RetryAfterUtc = DateTime.UtcNow.AddDays(Math.Clamp(_blacklistDays(), 1, 90));
                _states[hash] = entry;
                if (entry.Failures == Math.Clamp(_failureThreshold(), 1, 10))
                {
                    _logger.LogWarning("Artwork URL hash {Hash} blocked for {Days} days after {Threshold} failures", hash[..12], Math.Clamp(_blacklistDays(), 1, 90), Math.Clamp(_failureThreshold(), 1, 10));
                }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, JsonConvert.SerializeObject(entry) + Environment.NewLine);
                _journalLines++;
                if (_journalLines >= 512 && _journalLines > ((2L * _states.Count) + 512))
                {
                    CompactJournal();
                    _journalLines = _states.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist artwork failure journal");
            }
        }
    }

    private void CompactJournal()
    {
        var now = DateTime.UtcNow;
        foreach (var hash in _states.Where(x => x.Value.RetryAfterUtc <= now).Select(x => x.Key).ToArray())
        {
            _states.Remove(hash);
        }

        var temp = _path + ".tmp";
        try
        {
            File.WriteAllLines(temp, _states.Values.Select(JsonConvert.SerializeObject));
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public sealed class Entry
    {
        public string Hash { get; set; } = string.Empty;

        public int Failures { get; set; }

        public DateTime RetryAfterUtc { get; set; }
    }
}
