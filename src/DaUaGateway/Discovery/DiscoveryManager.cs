using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DaUaGateway.Config;
using DaUaGateway.Da;
using DaUaGateway.Inbox;
using DaUaGateway.Logging;
using DaUaGateway.Selection;
using DaUaGateway.Ua;
using TitaniumAS.Opc.Client.Common;

namespace DaUaGateway.Discovery
{
    /// <summary>
    /// Plug-and-play: периодически ищет DA-серверы (OpcEnum) и сам подключает новые.
    /// Источники берутся из трёх мест: автопоиск, список servers в gateway.json, папка «Входящие».
    /// </summary>
    public sealed class DiscoveryManager : IDisposable
    {
        private readonly GatewayConfig _cfg;
        private readonly GatewayNodeManager _ua;
        private readonly ManualTagStore _manual;
        private readonly SelectionStore _selection;
        private readonly ConcurrentDictionary<string, DaSourceSession> _sessions =
            new ConcurrentDictionary<string, DaSourceSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastHostError = new Dictionary<string, string>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loop;

        public DiscoveryManager(GatewayConfig cfg, GatewayNodeManager ua, ManualTagStore manual, SelectionStore selection)
        {
            _cfg = cfg;
            _ua = ua;
            _manual = manual;
            _selection = selection;
        }

        public int SourceCount => _sessions.Count;
        public IList<DaSourceSession> Sessions => _sessions.Values.OrderBy(s => s.Key).ToList();

        /// <summary>Найденные DA-серверы, для которых теги не выбраны — к ним конвертер НЕ подключается.</summary>
        public IList<string> IdleServers { get; private set; } = new List<string>();

        private readonly HashSet<string> _announcedIdle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

        private async Task RunAsync(CancellationToken ct)
        {
            int lastManualVersion = -1;
            int lastSelectionVersion = -1;
            var nextScan = DateTime.MinValue;

            while (!ct.IsCancellationRequested)
            {
                // сканируем по расписанию или сразу, если во «Входящих» появились новые источники
                if (DateTime.UtcNow >= nextScan || _manual.Version != lastManualVersion ||
                    _selection.Version != lastSelectionVersion)
                {
                    lastManualVersion = _manual.Version;
                    lastSelectionVersion = _selection.Version;
                    try { ScanOnce(); }
                    catch (Exception ex) { Log.Error("Ошибка автопоиска", ex); }
                    nextScan = DateTime.UtcNow.AddSeconds(Math.Max(5, _cfg.Discovery.ScanIntervalSec));
                }

                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void ScanOnce()
        {
            var candidates = new List<(string host, string progId, string origin)>();

            foreach (var r in _cfg.Servers.Where(r => r.Enabled && !string.IsNullOrEmpty(r.ProgId)))
                candidates.Add((r.Host ?? "localhost", r.ProgId, "конфиг"));

            foreach (var e in _manual.Sources.Where(e => !string.IsNullOrEmpty(e.ProgId)))
                candidates.Add((e.Host ?? "localhost", e.ProgId, "входящие"));

            foreach (var e in _selection.Entries.Where(e => !string.IsNullOrEmpty(e.ProgId)))
                candidates.Add((e.Host ?? "localhost", e.ProgId, "конфигуратор"));

            if (_cfg.Discovery.Enabled)
            {
                foreach (var host in _cfg.Discovery.Hosts.Where(h => !string.IsNullOrWhiteSpace(h)))
                {
                    try
                    {
                        var found = new OpcServerEnumeratorAuto().Enumerate(host, OpcServerCategory.OpcDaServers);
                        foreach (var d in found.Where(d => !string.IsNullOrEmpty(d.ProgId)))
                            candidates.Add((host, d.ProgId, "автопоиск"));
                        _lastHostError.Remove(host);
                    }
                    catch (Exception ex)
                    {
                        // не спамим лог одной и той же ошибкой каждые 30 секунд
                        if (!_lastHostError.TryGetValue(host, out var prev) || prev != ex.Message)
                            Log.Problem(Problems.DiscoveryFailed(host, ex));
                        _lastHostError[host] = ex.Message;
                    }
                }
            }

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var idle = new List<string>();

            foreach (var c in candidates)
            {
                var key = SourceKey.Make(c.host, c.progId);
                if (wanted.Contains(key) || idle.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;

                var rule = _cfg.FindRule(c.host, c.progId);
                if (rule != null && !rule.Enabled) continue;
                if (Wildcard.MatchesAny(c.progId, _cfg.Discovery.ExcludeProgIds)) continue;

                var settings = SourceSettings.Build(_cfg, c.host, c.progId, rule);

                // Подключаемся к DA-серверу ТОЛЬКО если для него что-то выбрано
                if (!IsWanted(key, rule, settings))
                {
                    idle.Add(key);
                    if (_announcedIdle.Add(key))
                        Log.Info($"Найден DA-сервер {key} — теги не выбраны, не подключаюсь (выберите теги в Конфигураторе)");
                    continue;
                }

                wanted.Add(key);
                _announcedIdle.Remove(key);
                if (_sessions.ContainsKey(key)) continue;

                var session = new DaSourceSession(settings, _ua, _manual, _selection);
                if (!_sessions.TryAdd(key, session)) continue;

                var sel = _selection.Get(key);
                bool auto = sel != null ? sel.AutoAll : settings.AutoMode;
                Log.Info($"Подключаю DA-сервер {key} ({c.origin}), режим: {(auto ? "все теги автоматически" : "выбранные теги")}");
                session.Start();
            }

            // Выбор снят — отключиться от DA-сервера и убрать его из UA
            foreach (var key in _sessions.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                if (!_sessions.TryRemove(key, out var session)) continue;
                try { session.Dispose(); } catch { }
                _ua.RemoveSource(key);
                _announcedIdle.Add(key);
                Log.Info($"[{key}] Теги больше не выбраны — DA-сервер отключён и убран из OPC UA");
            }

            IdleServers = idle;
        }

        /// <summary>Нужно ли подключаться к DA-серверу: выбраны теги в Конфигураторе / заданы в gateway.json / пришли через «Входящие».</summary>
        private bool IsWanted(string key, ServerRule rule, SourceSettings settings)
        {
            if (_manual.GetIncluded(key).Count > 0) return true;

            var sel = _selection.Get(key);
            if (sel != null) return sel.AutoAll || sel.Items.Count > 0 || sel.Folders.Count > 0;

            // выбора из Конфигуратора нет — действует gateway.json (mode auto или явный список tags)
            return settings.AutoMode || settings.Tags.Count > 0;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            foreach (var s in _sessions.Values)
            {
                try { s.Dispose(); } catch { }
            }
            _sessions.Clear();
        }
    }
}
