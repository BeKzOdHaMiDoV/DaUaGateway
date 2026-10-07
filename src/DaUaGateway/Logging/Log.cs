using System;
using System.IO;
using System.Linq;

namespace DaUaGateway.Logging
{
    /// <summary>Простой лог: консоль + файл logs/gateway-ГГГГММДД.log, старые файлы удаляются.</summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static string _dir;
        private static int _keepDays = 14;
        private static DateTime _lastCleanup = DateTime.MinValue;

        public static void Init(string dir, int keepDays)
        {
            _dir = dir;
            _keepDays = Math.Max(1, keepDays);
            Directory.CreateDirectory(_dir);
            Cleanup();
        }

        public static void Info(string msg) => Write("INFO ", msg, ConsoleColor.Gray);
        public static void Warn(string msg) => Write("WARN ", msg, ConsoleColor.Yellow);
        /// <summary>Проблема с кодом, причиной и советом (см. Problems).</summary>
        public static void Problem(Problem p, bool error = false) =>
            Write(error ? "ERROR" : "WARN ", p.ToString(), error ? ConsoleColor.Red : ConsoleColor.Yellow);

        public static void Error(string msg, Exception ex = null) =>
            Write("ERROR", ex == null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}", ConsoleColor.Red);

        private static void Write(string level, string msg, ConsoleColor color)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
            lock (Sync)
            {
                if (Environment.UserInteractive)
                {
                    try
                    {
                        var old = Console.ForegroundColor;
                        Console.ForegroundColor = color;
                        Console.WriteLine(line);
                        Console.ForegroundColor = old;
                    }
                    catch { /* в режиме окна-пульта консоли нет */ }
                }

                if (_dir == null) return;
                try
                {
                    File.AppendAllText(Path.Combine(_dir, $"gateway-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
                    // отдельный файл только с проблемами — чтобы не искать их среди тысяч строк
                    if (level != "INFO ")
                        File.AppendAllText(Path.Combine(_dir, $"errors-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
                    if ((DateTime.Now - _lastCleanup).TotalHours >= 24) Cleanup();
                }
                catch { /* лог не должен ронять шлюз */ }
            }
        }

        private static void Cleanup()
        {
            _lastCleanup = DateTime.Now;
            try
            {
                foreach (var f in new DirectoryInfo(_dir).GetFiles("*.log")
                             .Where(f => f.LastWriteTime < DateTime.Now.AddDays(-_keepDays)))
                    f.Delete();
            }
            catch { }
        }
    }
}
