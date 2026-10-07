using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DaUaGateway.Logging;

namespace DaUaGateway.Inbox
{
    /// <summary>
    /// Папка «Входящие»: кинули файл .csv/.txt — шлюз сам его прочитал и применил.
    ///
    /// Формат строк (разделитель ; или TAB, # — комментарий):
    ///   ProgId;ItemId            — добавить тег (DA-сервер на этом компьютере)
    ///   Host;ProgId;ItemId       — добавить тег удалённого DA-сервера
    ///   ProgId;!ItemId           — исключить тег (можно маску: !Random.*)
    ///
    /// Обработанный файл переносится в inbox/processed, с ошибками — в inbox/error (+ отчёт .log).
    /// </summary>
    public sealed class InboxWatcher : IDisposable
    {
        private readonly string _dir;
        private readonly ManualTagStore _store;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loop;

        public InboxWatcher(string dir, ManualTagStore store)
        {
            _dir = dir;
            _store = store;
        }

        public void Start()
        {
            Directory.CreateDirectory(_dir);
            Directory.CreateDirectory(Path.Combine(_dir, "processed"));
            Directory.CreateDirectory(Path.Combine(_dir, "error"));
            Log.Info($"Папка «Входящие»: {_dir}");
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var file in Directory.GetFiles(_dir)
                                 .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                    {
                        // файл ещё копируется — подождём
                        if ((DateTime.Now - File.GetLastWriteTime(file)).TotalSeconds < 2) continue;
                        ProcessFile(file);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Ошибка обработки папки «Входящие»", ex);
                }

                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void ProcessFile(string file)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file, DetectEncoding(file));
            }
            catch (IOException)
            {
                return; // файл занят — попробуем в следующий раз
            }

            var entries = new List<(string host, string progId, string itemId, bool exclude)>();
            var errors = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim().Trim('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#")) continue;

                var sep = line.Contains(';') ? ';' : '\t';
                var parts = line.Split(sep).Select(p => p.Trim().Trim('"')).ToArray();

                string host, progId, itemId;
                if (parts.Length == 2) { host = "localhost"; progId = parts[0]; itemId = parts[1]; }
                else if (parts.Length == 3) { host = parts[0]; progId = parts[1]; itemId = parts[2]; }
                else { errors.AppendLine($"Строка {i + 1}: ожидалось 2 или 3 колонки — «{line}»"); continue; }

                // пропускаем строку-заголовок из Excel
                if (progId.Equals("ProgId", StringComparison.OrdinalIgnoreCase)) continue;

                bool exclude = itemId.StartsWith("!");
                if (exclude) itemId = itemId.Substring(1).Trim();

                if (string.IsNullOrEmpty(progId) || string.IsNullOrEmpty(itemId))
                {
                    errors.AppendLine($"Строка {i + 1}: пустой ProgId или ItemId — «{line}»");
                    continue;
                }

                entries.Add((host, progId, itemId, exclude));
            }

            int changed = entries.Count > 0 ? _store.Apply(entries) : 0;
            var name = Path.GetFileName(file);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var targetDir = Path.Combine(_dir, entries.Count > 0 ? "processed" : "error");
            var target = Path.Combine(targetDir, $"{stamp}_{name}");

            try
            {
                File.Move(file, target);
                if (errors.Length > 0) File.WriteAllText(target + ".log", errors.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Error($"Не удалось переместить {name}", ex);
            }

            if (entries.Count > 0)
                Log.Info($"Входящие: файл {name} применён — строк: {entries.Count}, изменений: {changed}" +
                         (errors.Length > 0 ? ", есть ошибки (см. .log)" : ""));
            else
                Log.Warn($"Входящие: в файле {name} нет корректных строк — перенесён в error");
        }

        /// <summary>UTF-8 (в т.ч. с BOM) или Windows-1251 (так сохраняет Excel в русской Windows).</summary>
        private static Encoding DetectEncoding(string file)
        {
            var bytes = File.ReadAllBytes(file);
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                return Encoding.UTF8;
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1251);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
}
