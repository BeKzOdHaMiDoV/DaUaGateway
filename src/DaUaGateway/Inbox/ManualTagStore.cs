using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DaUaGateway.Config;
using DaUaGateway.Logging;
using Newtonsoft.Json;

namespace DaUaGateway.Inbox
{
    /// <summary>
    /// Теги, добавленные/исключённые через папку «Входящие».
    /// Хранятся в data/manual-tags.json и переживают перезапуск.
    /// </summary>
    public sealed class ManualTagStore
    {
        public sealed class SourceEntry
        {
            [JsonProperty("host")] public string Host { get; set; } = "localhost";
            [JsonProperty("progId")] public string ProgId { get; set; }
            [JsonProperty("include")] public List<string> Include { get; set; } = new List<string>();
            [JsonProperty("exclude")] public List<string> Exclude { get; set; } = new List<string>();
        }

        private readonly string _path;
        private readonly object _sync = new object();
        private Dictionary<string, SourceEntry> _entries =
            new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
        private int _version;

        public ManualTagStore(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, SourceEntry>>(File.ReadAllText(path));
                    if (loaded != null)
                        _entries = new Dictionary<string, SourceEntry>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Не удалось прочитать {path}", ex);
            }
        }

        /// <summary>Растёт при каждом изменении — сессии по нему понимают, что надо пересобрать теги.</summary>
        public int Version => Volatile.Read(ref _version);

        public IList<SourceEntry> Sources
        {
            get { lock (_sync) return _entries.Values.ToList(); }
        }

        public IList<string> GetIncluded(string key)
        {
            lock (_sync) return _entries.TryGetValue(key, out var e) ? e.Include.ToList() : new List<string>();
        }

        public bool IsExcluded(string key, string itemId)
        {
            lock (_sync)
                return _entries.TryGetValue(key, out var e) &&
                       e.Exclude.Any(x => string.Equals(x, itemId, StringComparison.Ordinal) || Wildcard.IsMatch(itemId, x));
        }

        /// <summary>Применяет строки из файла. Возвращает число реально изменённых записей.</summary>
        public int Apply(IEnumerable<(string host, string progId, string itemId, bool exclude)> lines)
        {
            int changed = 0;
            lock (_sync)
            {
                foreach (var l in lines)
                {
                    var key = SourceKey.Make(l.host, l.progId);
                    if (!_entries.TryGetValue(key, out var e))
                        _entries[key] = e = new SourceEntry { Host = l.host, ProgId = l.progId };

                    var target = l.exclude ? e.Exclude : e.Include;
                    var other = l.exclude ? e.Include : e.Exclude;
                    if (!target.Contains(l.itemId)) { target.Add(l.itemId); changed++; }
                    if (other.Remove(l.itemId)) changed++;
                }

                if (changed > 0)
                {
                    Save();
                    Interlocked.Increment(ref _version);
                }
            }
            return changed;
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(_entries, Formatting.Indented));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                Log.Error($"Не удалось сохранить {_path}", ex);
            }
        }
    }
}
