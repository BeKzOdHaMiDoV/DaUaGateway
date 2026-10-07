using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DaUaGateway.Logging;
using Newtonsoft.Json;

namespace DaUaGateway.Selection
{
    /// <summary>Файл data/selection.json — выбор тегов, сделанный в Конфигураторе.</summary>
    public class SelectionFile
    {
        [JsonProperty("servers")]
        public Dictionary<string, SelectionEntry> Servers { get; set; } =
            new Dictionary<string, SelectionEntry>(StringComparer.OrdinalIgnoreCase);
    }

    public class SelectionEntry
    {
        [JsonProperty("host")] public string Host { get; set; } = "localhost";
        [JsonProperty("progId")] public string ProgId { get; set; }

        /// <summary>Галочка «Переводить все теги автоматически».</summary>
        [JsonProperty("autoAll")] public bool AutoAll { get; set; }

        /// <summary>Выбранные папки: всё внутри, включая теги, которые появятся позже.</summary>
        [JsonProperty("folders")] public List<string[]> Folders { get; set; } = new List<string[]>();

        /// <summary>Отдельно выбранные теги.</summary>
        [JsonProperty("items")] public List<SelectedItem> Items { get; set; } = new List<SelectedItem>();

        /// <summary>Снятые папки внутри выбранной папки / режима «все теги».</summary>
        [JsonProperty("excludeFolders")] public List<string[]> ExcludeFolders { get; set; } = new List<string[]>();

        /// <summary>Снятые теги внутри выбранной папки / режима «все теги».</summary>
        [JsonProperty("excludeItems")] public List<SelectedItem> ExcludeItems { get; set; } = new List<SelectedItem>();

        /// <summary>Выбрано ли содержимое папки: побеждает самое «глубокое» правило (как в Конфигураторе).</summary>
        public bool FolderSelected(string[] path)
        {
            int best = -1;
            bool selected = false;
            if (AutoAll) { best = 0; selected = true; }
            foreach (var f in Folders ?? new List<string[]>())
                if (IsUnder(path, f) && f.Length > best) { best = f.Length; selected = true; }
            foreach (var f in ExcludeFolders ?? new List<string[]>())
                if (IsUnder(path, f) && f.Length >= best) { best = f.Length; selected = false; }
            return selected;
        }

        public bool ItemSelected(string id, string[] path)
        {
            if ((ExcludeItems ?? new List<SelectedItem>()).Any(i => i.Id == id)) return false;
            if (Items.Any(i => i.Id == id)) return true;
            return FolderSelected(path ?? new string[0]);
        }

        public static bool IsUnder(string[] path, string[] root)
        {
            if (path == null || root == null || path.Length < root.Length) return false;
            for (int i = 0; i < root.Length; i++)
                if (!string.Equals(path[i], root[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    public class SelectedItem
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("path")] public string[] Path { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
    }

    /// <summary>
    /// Следит за data/selection.json. Конфигуратор сохранил файл → через ~2 секунды
    /// растёт Version, и сессии DA пересобирают набор тегов без перезапуска.
    /// </summary>
    public sealed class SelectionStore : IDisposable
    {
        private readonly string _path;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private SelectionFile _current = new SelectionFile();
        private DateTime _lastWrite = DateTime.MinValue;
        private int _version;
        private Task _loop;

        public SelectionStore(string path)
        {
            _path = path;
            Reload(initial: true);
        }

        public int Version => Volatile.Read(ref _version);

        public bool Exists => File.Exists(_path);

        public SelectionEntry Get(string key)
        {
            var snapshot = Volatile.Read(ref _current);
            return snapshot.Servers.TryGetValue(key, out var e) ? e : null;
        }

        public IList<SelectionEntry> Entries => Volatile.Read(ref _current).Servers.Values.ToList();

        public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
                Reload(initial: false);
            }
        }

        private void Reload(bool initial)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    if (_lastWrite != DateTime.MinValue)
                    {
                        _lastWrite = DateTime.MinValue;
                        Volatile.Write(ref _current, new SelectionFile());
                        Interlocked.Increment(ref _version);
                        Log.Info("Файл выбора тегов удалён — действуют настройки gateway.json");
                    }
                    return;
                }

                var stamp = File.GetLastWriteTimeUtc(_path);
                if (stamp == _lastWrite) return;

                var loaded = JsonConvert.DeserializeObject<SelectionFile>(File.ReadAllText(_path)) ?? new SelectionFile();
                loaded.Servers = new Dictionary<string, SelectionEntry>(loaded.Servers ?? new Dictionary<string, SelectionEntry>(),
                    StringComparer.OrdinalIgnoreCase);

                _lastWrite = stamp;
                Volatile.Write(ref _current, loaded);
                Interlocked.Increment(ref _version);

                Log.Info((initial ? "Загружен" : "Обновлён") + " выбор тегов из Конфигуратора: " +
                         string.Join(", ", loaded.Servers.Select(kv => kv.Key + (kv.Value.AutoAll
                             ? " (все теги)"
                             : $" ({kv.Value.Items.Count} тегов, {kv.Value.Folders.Count} папок)"))));
            }
            catch (IOException)
            {
                // Конфигуратор ещё пишет файл — прочитаем в следующий раз
            }
            catch (Exception ex)
            {
                Log.Problem(Problems.SelectionFileBroken(_path, ex));
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
    }
}
