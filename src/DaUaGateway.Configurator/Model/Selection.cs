using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace DaUaGateway.Configurator.Model
{
    // Тот же формат, что читает шлюз (DaUaGateway/Selection/SelectionStore.cs) — data/selection.json

    public class SelectionFile
    {
        [JsonProperty("servers")]
        public Dictionary<string, SelectionEntry> Servers { get; set; } =
            new Dictionary<string, SelectionEntry>(StringComparer.OrdinalIgnoreCase);

        public static SelectionFile Load(string path)
        {
            if (!File.Exists(path)) return new SelectionFile();
            var f = JsonConvert.DeserializeObject<SelectionFile>(File.ReadAllText(path)) ?? new SelectionFile();
            f.Servers = new Dictionary<string, SelectionEntry>(f.Servers ?? new Dictionary<string, SelectionEntry>(),
                StringComparer.OrdinalIgnoreCase);
            return f;
        }

        /// <summary>Атомарная запись: шлюз никогда не увидит половину файла.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public SelectionEntry GetOrCreate(string progId)
        {
            if (!Servers.TryGetValue(progId, out var e))
                Servers[progId] = e = new SelectionEntry { Host = "localhost", ProgId = progId };
            return e;
        }
    }

    public class SelectionEntry
    {
        [JsonProperty("host")] public string Host { get; set; } = "localhost";
        [JsonProperty("progId")] public string ProgId { get; set; }
        [JsonProperty("autoAll")] public bool AutoAll { get; set; }
        [JsonProperty("folders")] public List<string[]> Folders { get; set; } = new List<string[]>();
        [JsonProperty("items")] public List<SelectedItem> Items { get; set; } = new List<SelectedItem>();

        // исключения внутри выбранных папок / режима «все теги»
        [JsonProperty("excludeFolders")] public List<string[]> ExcludeFolders { get; set; } = new List<string[]>();
        [JsonProperty("excludeItems")] public List<SelectedItem> ExcludeItems { get; set; } = new List<SelectedItem>();

        public bool IsEmpty => !AutoAll && Folders.Count == 0 && Items.Count == 0;

        public int ExcludedCount => ExcludeFolders.Count + ExcludeItems.Count;

        // ---------------------------------------------------------------- состояние

        /// <summary>
        /// Выбрано ли содержимое папки path: побеждает самое «глубокое» правило
        /// (выбранная папка / исключённая папка / режим «все теги» как корень).
        /// </summary>
        public bool FolderSelected(string[] path)
        {
            int best = -1;
            bool selected = false;
            if (AutoAll) { best = 0; selected = true; }
            foreach (var f in Folders)
                if (IsUnder(path, f) && f.Length > best) { best = f.Length; selected = true; }
            foreach (var f in ExcludeFolders)
                if (IsUnder(path, f) && f.Length >= best) { best = f.Length; selected = false; }
            return selected;
        }

        public bool ItemSelected(string id, string[] parentPath)
        {
            if (ExcludeItems.Any(i => i.Id == id)) return false;
            if (Items.Any(i => i.Id == id)) return true;
            return FolderSelected(parentPath);
        }

        /// <summary>Внутри папки есть правила, которые меняют её состояние, — показать «частично».</summary>
        public bool FolderPartial(string[] path)
        {
            bool own = FolderSelected(path);
            if (own)
                return ExcludeFolders.Any(f => f.Length > path.Length && IsUnder(f, path)) ||
                       ExcludeItems.Any(i => IsUnder(i.Path ?? new string[0], path));
            return Folders.Any(f => f.Length > path.Length && IsUnder(f, path)) ||
                   Items.Any(i => IsUnder(i.Path ?? new string[0], path));
        }

        // ---------------------------------------------------------------- изменения

        public void SetFolder(string[] path, bool on)
        {
            // всё, что было отмечено/снято внутри, — сбрасываем: папка становится целиком «да» или «нет»
            Folders.RemoveAll(f => f.Length > path.Length && IsUnder(f, path));
            ExcludeFolders.RemoveAll(f => f.Length > path.Length && IsUnder(f, path));
            Items.RemoveAll(i => IsUnder(i.Path ?? new string[0], path));
            ExcludeItems.RemoveAll(i => IsUnder(i.Path ?? new string[0], path));
            Folders.RemoveAll(f => Same(f, path));
            ExcludeFolders.RemoveAll(f => Same(f, path));

            bool inherited = FolderSelected(path); // что даёт родитель после очистки
            if (on && !inherited) Folders.Add(path);
            if (!on && inherited) ExcludeFolders.Add(path);
        }

        public void SetItem(string id, string[] parentPath, string name, bool on)
        {
            Items.RemoveAll(i => i.Id == id);
            ExcludeItems.RemoveAll(i => i.Id == id);
            bool inherited = FolderSelected(parentPath);
            if (on && !inherited) Items.Add(new SelectedItem { Id = id, Path = parentPath, Name = name });
            if (!on && inherited) ExcludeItems.Add(new SelectedItem { Id = id, Path = parentPath, Name = name });
        }

        public bool HasItem(string id) => Items.Any(i => i.Id == id);

        public void Clear()
        {
            AutoAll = false;
            Folders.Clear(); Items.Clear(); ExcludeFolders.Clear(); ExcludeItems.Clear();
        }

        /// <summary>path лежит внутри root (или совпадает с ним).</summary>
        public static bool IsUnder(string[] path, string[] root)
        {
            if (path == null || root == null || path.Length < root.Length) return false;
            for (int i = 0; i < root.Length; i++)
                if (!string.Equals(path[i], root[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool Same(string[] a, string[] b) => a.Length == b.Length && IsUnder(a, b);
    }

    public class SelectedItem
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("path")] public string[] Path { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
    }
}
