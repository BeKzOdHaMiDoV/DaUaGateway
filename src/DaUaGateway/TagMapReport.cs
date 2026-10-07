using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DaUaGateway.Config;

namespace DaUaGateway
{
    /// <summary>
    /// Команда DaUaGateway.exe --list — показывает в терминале, какие теги DA переведены в OPC UA.
    /// Читает файлы data/tag-map/*.csv, поэтому работает и когда шлюз запущен как служба.
    ///
    ///   DaUaGateway.exe --list            сводка + первые 20 тегов каждого DA-сервера
    ///   DaUaGateway.exe --list all        все теги
    ///   DaUaGateway.exe --list "Motor*"   только теги по маске (по имени тега в DA)
    /// </summary>
    public static class TagMapReport
    {
        private const int DefaultLimit = 20;

        public static int Run(string[] args)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            GatewayConfig cfg;
            try { cfg = GatewayConfig.Load(Path.Combine(baseDir, "gateway.json")); }
            catch { cfg = new GatewayConfig(); }

            var dir = Path.Combine(baseDir, cfg.DataDir, "tag-map");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.csv").OrderBy(f => f).ToArray() : new string[0];

            string mask = args.FirstOrDefault();
            bool all = string.Equals(mask, "all", StringComparison.OrdinalIgnoreCase);
            if (all) mask = null;

            Console.WriteLine();
            Write("  DA → OPC UA: переведённые теги", ConsoleColor.White);
            Console.WriteLine("  UA-сервер: " + cfg.Ua.Endpoint.Replace("localhost", Environment.MachineName));
            Console.WriteLine();

            if (files.Length == 0)
            {
                Write("  Список ещё не создан. Запустите шлюз (службу или DaUaGateway.exe)", ConsoleColor.Yellow);
                Write("  и подождите, пока он подключится к DA-серверам.", ConsoleColor.Yellow);
                return 1;
            }

            int totalOk = 0, totalRejected = 0;
            foreach (var file in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                catch (IOException) { Write("  Файл занят, повторите команду: " + file, ConsoleColor.Yellow); continue; }

                var info = lines.FirstOrDefault(l => l.StartsWith("#")) ?? "";
                var rows = lines.Where(l => !l.StartsWith("#")).Skip(1)
                                .Select(ParseCsv).Where(r => r.Count >= 5).ToList();

                var source = Between(info, "DA-сервер:", ";") ?? Path.GetFileNameWithoutExtension(file);
                var updated = Between(info, "обновлено:", null) ?? "";
                int ok = rows.Count(r => r[4] == "OK");
                int rejected = rows.Count - ok;
                totalOk += ok;
                totalRejected += rejected;

                Write($"■ {source}", ConsoleColor.Cyan);
                Console.WriteLine($"  переведено: {ok}   отклонено: {rejected}   обновлено: {updated.Trim()}");

                var shown = mask == null ? rows : rows.Where(r => Wildcard.IsMatch(r[0], mask)).ToList();
                int limit = all || mask != null ? int.MaxValue : DefaultLimit;
                int width = Math.Min(48, Math.Max(12, shown.Take(limit).Select(r => r[0].Length).DefaultIfEmpty(12).Max()));

                foreach (var r in shown.Take(limit))
                {
                    var line = $"    {Pad(r[0], width)}  →  {r[1]}   ({r[3]})";
                    if (r[4] == "OK") Console.WriteLine(line);
                    else Write(line + "   ОТКЛОНЁН DA-сервером", ConsoleColor.Red);
                }

                if (mask != null && shown.Count == 0)
                    Console.WriteLine("    (по маске «" + mask + "» ничего не найдено)");
                if (shown.Count > limit)
                    Write($"    … ещё {shown.Count - limit}. Все: DaUaGateway.exe --list all   |   по маске: DaUaGateway.exe --list \"Имя*\"",
                        ConsoleColor.DarkGray);
                Console.WriteLine("    файл: " + file);
                Console.WriteLine();
            }

            Write($"  ИТОГО: DA-серверов {files.Length}, тегов переведено {totalOk}, отклонено {totalRejected}", ConsoleColor.Green);
            Console.WriteLine();
            return 0;
        }

        private static void Write(string text, ConsoleColor color)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }

        private static string Pad(string s, int width) =>
            s.Length > width ? s.Substring(0, width - 1) + "…" : s.PadRight(width);

        private static string Between(string text, string start, string end)
        {
            int i = text.IndexOf(start, StringComparison.Ordinal);
            if (i < 0) return null;
            i += start.Length;
            int j = end == null ? text.Length : text.IndexOf(end, i, StringComparison.Ordinal);
            if (j < 0) j = text.Length;
            return text.Substring(i, j - i).Trim();
        }

        /// <summary>Разбор строки CSV с разделителем ; и кавычками.</summary>
        private static List<string> ParseCsv(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ';') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            result.Add(sb.ToString());
            return result;
        }
    }
}
