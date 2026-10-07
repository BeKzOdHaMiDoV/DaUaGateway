using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DaUaGateway.Configurator.Services
{
    /// <summary>Читает новые строки журнала конвертера (logs/gateway-ГГГГММДД.log) — для панели «Журнал».</summary>
    public sealed class LogTail
    {
        private readonly string _dir;
        private string _file;
        private long _position;

        public LogTail(string logsDir) { _dir = logsDir; }

        /// <summary>Новые строки с прошлого вызова. При первом вызове — последние initialLines строк.</summary>
        public List<string> ReadNew(int initialLines = 15)
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(_dir)) return result;
                var latest = new DirectoryInfo(_dir).GetFiles("gateway-*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (latest == null) return result;

                bool first = _file == null;
                if (!string.Equals(latest.FullName, _file, StringComparison.OrdinalIgnoreCase))
                {
                    _file = latest.FullName;
                    _position = 0;
                }

                using (var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < _position) _position = 0; // файл пересоздан
                    fs.Seek(_position, SeekOrigin.Begin);
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        var text = reader.ReadToEnd();
                        _position = fs.Length;
                        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        if (first && lines.Count > initialLines) lines = lines.Skip(lines.Count - initialLines).ToList();
                        result.AddRange(lines);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }
    }
}
