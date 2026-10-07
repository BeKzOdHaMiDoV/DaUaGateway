using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DaUaGateway.Config;
using DaUaGateway.Inbox;
using DaUaGateway.Logging;
using DaUaGateway.Selection;
using DaUaGateway.Ua;
using Opc.Ua;
using TitaniumAS.Opc.Client.Common;
using TitaniumAS.Opc.Client.Da;

namespace DaUaGateway.Da
{
    /// <summary>
    /// Одно подключение к одному DA-серверу:
    /// подключиться → найти теги (автообзор / ручной список / «Входящие») → подписаться →
    /// следить за связью → периодически пересобирать список тегов → при обрыве переподключаться.
    /// </summary>
    public sealed class DaSourceSession : IDisposable
    {
        private const int AddBatchSize = 500;

        private readonly SourceSettings _s;
        private readonly GatewayNodeManager _ua;
        private readonly ManualTagStore _manual;
        private readonly SelectionStore _selection;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _comLock = new object();

        private readonly Dictionary<string, OpcDaItem> _items = new Dictionary<string, OpcDaItem>(StringComparer.Ordinal);
        private readonly HashSet<string> _failed = new HashSet<string>(StringComparer.Ordinal);

        private OpcDaServer _server;
        private OpcDaGroup _group;
        private volatile bool _shutdownRequested;

        // timestampSource=auto: последнее значение и время DA по каждому тегу
        private readonly Dictionary<string, (object value, DateTime daTs)> _lastDa =
            new Dictionary<string, (object value, DateTime daTs)>(StringComparer.Ordinal);
        // теги, у которых DA-сервер не обновляет время, — для них берём время шлюза
        private readonly HashSet<string> _gatewayTimeItems = new HashSet<string>(StringComparer.Ordinal);

        // теги с плохим качеством (Bad) — для сообщения E303 «нет связи DA-сервера с оборудованием»
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OpcDaQualityStatus> _badItems =
            new System.Collections.Concurrent.ConcurrentDictionary<string, OpcDaQualityStatus>(StringComparer.Ordinal);
        private int _badReported;
        private DateTime _badReportedAt = DateTime.MinValue;
        private bool _timeWarned;

        // чтобы одна и та же ошибка связи не заполняла лог каждые 5 секунд
        private string _lastError;
        private DateTime _lastErrorLogged = DateTime.MinValue;
        private int _manualVersionSeen = -1;
        private int _selectionVersionSeen = -1;
        private bool _reportedOnce;
        private Task _loop;

        public DaSourceSession(SourceSettings settings, GatewayNodeManager ua, ManualTagStore manual, SelectionStore selection)
        {
            _s = settings;
            _ua = ua;
            _manual = manual;
            _selection = selection;
        }

        public string Key => _s.Key;
        public bool Connected { get; private set; }
        public int TagCount => _items.Count;
        public int RejectedCount => _failed.Count;
        public string LastError { get; private set; }

        public void Start()
        {
            _ua.EnsureSource(_s.Key, _s.DisplayName);
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _ua.SetSourceStatus(Key, false, "Подключение...");
                    Connect();
                    SyncTags();
                    Connected = true;
                    LastError = null;
                    if (_lastError != null) Log.Info($"[{Key}] Связь с DA-сервером восстановлена");
                    _lastError = null;
                    var lastBrowse = DateTime.UtcNow;

                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(_s.HealthCheckMs, ct);
                        Heartbeat();
                        ReportQuality();

                        bool rebrowseDue = NeedsBrowse() && _s.RebrowseIntervalSec > 0 &&
                                           (DateTime.UtcNow - lastBrowse).TotalSeconds >= _s.RebrowseIntervalSec;
                        if (rebrowseDue || _manual.Version != _manualVersionSeen || _selection.Version != _selectionVersionSeen)
                        {
                            SyncTags();
                            lastBrowse = DateTime.UtcNow;
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    var problem = Problems.FromDaException(Key, ex);
                    var msg = problem.Code + "|" + problem.Raw;
                    bool same = msg == _lastError;
                    if (!same || (DateTime.UtcNow - _lastErrorLogged).TotalMinutes >= 10)
                    {
                        if (same) problem.Action += " (ошибка повторяется, конвертер пробует каждые " + _s.ReconnectDelayMs / 1000 + " с)";
                        Log.Problem(problem);
                        _lastErrorLogged = DateTime.UtcNow;
                    }
                    _lastError = msg;
                    LastError = "[" + problem.Code + "] " + problem.Title;
                }
                Connected = false;

                _ua.SetAllTagsStatus(Key, StatusCodes.BadNotConnected);
                _ua.SetSourceStatus(Key, false, "Нет связи, переподключение...");
                Disconnect();

                try { await Task.Delay(_s.ReconnectDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        // ------------------------------------------------------------------ подключение

        private void Connect()
        {
            lock (_comLock)
            {
                _shutdownRequested = false;
                _items.Clear();
                lock (_lastDa) _lastDa.Clear();
                _failed.Clear();

                _server = new OpcDaServer(UrlBuilder.Build(_s.ProgId, _s.Host));
                _server.Connect();
                _server.Shutdown += OnShutdown;

                _group = _server.AddGroup("DaUaGateway");
                _group.IsActive = true;
                _group.UpdateRate = TimeSpan.FromMilliseconds(_s.UpdateRateMs);
                _group.ValuesChanged += OnValuesChanged;
            }
            Log.Info($"[{Key}] Подключено к DA-серверу");
        }

        private void Disconnect()
        {
            lock (_comLock)
            {
                try
                {
                    if (_group != null) _group.ValuesChanged -= OnValuesChanged;
                    if (_server != null)
                    {
                        _server.Shutdown -= OnShutdown;
                        _server.Dispose();
                    }
                }
                catch { /* сервер уже недоступен */ }

                _group = null;
                _server = null;
                _items.Clear();
            }
        }

        private void OnShutdown(object sender, OpcShutdownEventArgs e)
        {
            Log.Problem(Problems.DaShutdown(Key, e.Reason));
            _shutdownRequested = true;
        }

        /// <summary>Проверка связи: COM-вызов упадёт, если DA-сервер умер.</summary>
        private void Heartbeat()
        {
            if (_shutdownRequested) throw new InvalidOperationException("DA-сервер завершил работу");

            lock (_comLock)
            {
                var first = _items.Values.FirstOrDefault();
                if (first != null)
                    _group.Read(new[] { first }, OpcDaDataSource.Cache);
                else if (_server == null || !_server.IsConnected)
                    throw new InvalidOperationException("Соединение с DA-сервером потеряно");
            }
        }

        // ------------------------------------------------------------------ набор тегов

        private sealed class Desired
        {
            public string ItemId;
            public string[] Path;
            public string Name;
        }

        /// <summary>Собирает нужный список тегов и приводит к нему DA-группу и UA-дерево.</summary>
        private void SyncTags()
        {
            int manualVersion = _manual.Version;
            int selectionVersion = _selection.Version;
            var desired = BuildDesiredList();
            var desiredIds = new HashSet<string>(desired.Select(d => d.ItemId), StringComparer.Ordinal);

            lock (_comLock)
            {
                // убрать исчезнувшие
                var toRemove = _items.Where(kv => !desiredIds.Contains(kv.Key)).ToList();
                if (toRemove.Count > 0)
                {
                    try { _group.RemoveItems(toRemove.Select(kv => kv.Value).ToList()); }
                    catch (Exception ex) { Log.Warn($"[{Key}] Ошибка удаления тегов из DA-группы: {ex.Message}"); }
                    foreach (var kv in toRemove) _items.Remove(kv.Key);
                }
                _failed.RemoveWhere(id => !desiredIds.Contains(id));

                // добавить новые пачками
                var toAdd = desired.Where(d => !_items.ContainsKey(d.ItemId) && !_failed.Contains(d.ItemId)).ToList();
                int rejectedLogged = 0;
                for (int i = 0; i < toAdd.Count; i += AddBatchSize)
                {
                    var batch = toAdd.Skip(i).Take(AddBatchSize).ToList();
                    var defs = batch.Select(d => new OpcDaItemDefinition { ItemId = d.ItemId, IsActive = true }).ToArray();
                    OpcDaItemResult[] results = _group.AddItems(defs);

                    for (int j = 0; j < results.Length; j++)
                    {
                        if (results[j].Error.Failed || results[j].Item == null)
                        {
                            _failed.Add(defs[j].ItemId);
                            // подробно — первые 10, дальше одной строкой, чтобы не засорять журнал
                            if (++rejectedLogged <= 10) Log.Problem(Problems.ItemRejected(Key, defs[j].ItemId, (int)results[j].Error));
                        }
                        else
                        {
                            _items[defs[j].ItemId] = results[j].Item;
                        }
                    }
                }
                if (rejectedLogged > 10)
                    Log.Warn($"[{Key}] … и ещё {rejectedLogged - 10} тег(ов) не принято DA-сервером (полный список — в data\\tag-map, статус «Отклонён»)");
            }

            // описание тегов для UA (тип и права берём из DA)
            var tags = new List<TagInfo>(desired.Count);
            foreach (var d in desired)
            {
                var t = new TagInfo { ItemId = d.ItemId, Path = d.Path, Name = d.Name };
                if (_items.TryGetValue(d.ItemId, out var item))
                {
                    TypeMapper.Map(item.CanonicalDataType, out t.DataType, out t.ValueRank);
                    t.DaType = item.CanonicalDataType?.Name ?? "";
                    t.Writable = _s.AllowWrite && (item.AccessRights & OpcDaAccessRights.Write) != 0;
                }
                else
                {
                    t.Failed = true;
                    t.DataType = DataTypeIds.BaseDataType;
                    t.ValueRank = ValueRanks.Any;
                }
                tags.Add(t);
            }

            var (added, removed) = _ua.SyncTags(Key, tags, _s.AllowWrite ? Write : (TagWriteHandler)null);
            _manualVersionSeen = manualVersion;
            _selectionVersionSeen = selectionVersion;

            _ua.SetSourceStatus(Key, true, $"Подключено, тегов: {_items.Count}" +
                                           (_failed.Count > 0 ? $", отклонено: {_failed.Count}" : ""));
            if (added > 0 || removed > 0 || _reportedOnce == false)
            {
                _reportedOnce = true;
                WriteTagMap(tags);
                if (tags.Count == 0)
                    Log.Info($"[{Key}] Теги для конвертации не выбраны — откройте «DaUaGateway Конфигуратор» и отметьте нужные");
                else
                    PrintReport(tags, added, removed);
            }
        }

        // ------------------------------------------------------------------ список тегов DA → UA

        private string UaNodeId(string itemId) => $"ns={_ua.NamespaceIndex};s={Key}|{itemId}";

        private string UaPath(TagInfo t) =>
            "Objects/Servers/" + Key + "/" +
            (t.Path != null && t.Path.Length > 0 ? string.Join("/", t.Path) + "/" : "") + t.Name;

        public static string MapFileName(string key)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
            return key + ".csv";
        }

        /// <summary>Файл data/tag-map/&lt;источник&gt;.csv — полный список «тег DA → узел UA» (для SCADA и для --list).</summary>
        private void WriteTagMap(List<TagInfo> tags)
        {
            try
            {
                Directory.CreateDirectory(_s.MapDir);
                var sb = new StringBuilder();
                sb.AppendLine($"# DA-сервер: {Key}; UA: {_s.Endpoint}; namespace: {_s.NamespaceUri} (ns={_ua.NamespaceIndex}); обновлено: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("DA_ItemID;UA_NodeId;UA_Путь;Тип;Статус");
                foreach (var t in tags)
                    sb.AppendLine(string.Join(";", Csv(t.ItemId), Csv(UaNodeId(t.ItemId)), Csv(UaPath(t)),
                        Csv(t.DaType ?? ""), t.Failed ? "Отклонён" : "OK"));
                File.WriteAllText(Path.Combine(_s.MapDir, MapFileName(Key)), sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log.Warn($"[{Key}] Не удалось записать список тегов: {ex.Message}");
            }
        }

        /// <summary>Наглядный отчёт в консоль/лог: что переведено и как это выглядит в UA.</summary>
        private void PrintReport(List<TagInfo> tags, int added, int removed)
        {
            int ok = tags.Count(t => !t.Failed);
            var endpoint = _s.Endpoint.Replace("localhost", Environment.MachineName);
            Log.Info($"[{Key}] ══ DA → UA: переведено тегов {ok}, отклонено {_failed.Count}" +
                     (removed > 0 ? $", удалено {removed}" : "") + " ══");
            Log.Info($"[{Key}]    UA-сервер: {endpoint}   папка: Objects/Servers/{Key}");
            foreach (var t in tags.Where(t => !t.Failed).Take(5))
                Log.Info($"[{Key}]    {t.ItemId}  →  {UaNodeId(t.ItemId)}  ({t.DaType})");
            if (ok > 5)
                Log.Info($"[{Key}]    … и ещё {ok - 5}. Полный список: {Path.Combine(_s.MapDir, MapFileName(Key))}  или команда: DaUaGateway.exe --list");
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ';', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        private List<Desired> BuildDesiredList()
        {
            var result = new List<Desired>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var manualSeen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string itemId, string[] path, string name, string source)
            {
                if (string.IsNullOrWhiteSpace(itemId)) return;
                if (_manual.IsExcluded(Key, itemId)) return;
                // предупреждаем только о повторе внутри ручных списков;
                // ручной тег, который уже нашёл автообзор, — это нормально
                if (source != "browse" && !manualSeen.Add(itemId))
                    Log.Warn($"[{Key}] Дубликат тега в ручном списке пропущен: {itemId}");
                if (!seen.Add(itemId)) return;
                if (result.Count >= _s.MaxTags)
                {
                    if (result.Count == _s.MaxTags) Log.Warn($"[{Key}] Достигнут лимит тегов ({_s.MaxTags}), остальные пропущены");
                    return;
                }
                result.Add(new Desired { ItemId = itemId, Path = path, Name = name });
            }

            // Выбор из Конфигуратора важнее настроек gateway.json
            var sel = _selection.Get(Key);
            bool autoAll = sel != null ? sel.AutoAll : _s.AutoMode;

            // 1. Обзор дерева: «все теги» или выбранные папки (включая теги, появившиеся позже)
            if (NeedsBrowse())
            {
                List<DaBrowser.BrowsedItem> browsed;
                lock (_comLock) browsed = DaBrowser.BrowseAll(_server, Math.Max(_s.MaxTags * 5, 1000));
                foreach (var b in browsed)
                {
                    bool take = sel != null
                        ? sel.ItemSelected(b.ItemId, b.Path)               // папки, исключения, «все теги» — из Конфигуратора
                        : Wildcard.Passes(b.ItemId, _s.Include, _s.Exclude); // без Конфигуратора — маски gateway.json
                    if (take) Add(b.ItemId, b.Path, b.Name, "browse");
                }
            }

            // 2. Отдельно отмеченные в Конфигураторе теги
            if (sel != null)
                foreach (var item in sel.Items.Where(i => !string.IsNullOrWhiteSpace(i.Id)))
                    if (sel.ItemSelected(item.Id, item.Path))
                        Add(item.Id, item.Path ?? SplitPath(item.Id), item.Name ?? LastSegment(item.Id), "selection");

            // 2. Ручной список из конфига + 3. теги из «Входящих»
            foreach (var id in _s.Tags.Concat(_manual.GetIncluded(Key)))
                Add(id, SplitPath(id), LastSegment(id), "manual");

            return result;
        }

        /// <summary>Нужен ли обзор всего дерева: режим «все теги» или выбраны папки.</summary>
        private bool NeedsBrowse()
        {
            var sel = _selection.Get(Key);
            if (sel == null) return _s.AutoMode;
            return sel.AutoAll || sel.Folders.Count > 0;
        }

        /// <summary>E303: сообщить, когда теги ушли в Bad, и когда качество восстановилось (без спама).</summary>
        private void ReportQuality()
        {
            // теги, которые уже сняты с конвертации, не считаем
            foreach (var id in _badItems.Keys.Where(k => !_items.ContainsKey(k)).ToList()) _badItems.TryRemove(id, out _);

            int count = _badItems.Count;
            if (count > 0 && (_badReported == 0 || (DateTime.UtcNow - _badReportedAt).TotalMinutes >= 10))
            {
                var first = _badItems.First();
                Log.Problem(Problems.BadQuality(Key, count, first.Key, QualityReason(first.Value)));
                _badReported = count;
                _badReportedAt = DateTime.UtcNow;
            }
            else if (count == 0 && _badReported > 0)
            {
                Log.Info($"[{Key}] Качество тегов восстановилось — все переводимые теги снова Good");
                _badReported = 0;
            }
        }

        private static string QualityReason(OpcDaQualityStatus s)
        {
            switch (s)
            {
                case OpcDaQualityStatus.BadCommFailure: return "нет связи с устройством (Comm Failure)";
                case OpcDaQualityStatus.BadNotConnected: return "вход не подключён (Not Connected)";
                case OpcDaQualityStatus.BadDeviceFailure: return "отказ устройства (Device Failure)";
                case OpcDaQualityStatus.BadSensorFailure: return "отказ датчика (Sensor Failure)";
                case OpcDaQualityStatus.BadConfigurationError: return "ошибка настройки тега в DA-сервере (Configuration Error)";
                case OpcDaQualityStatus.BadOutOfService: return "тег или устройство выведены из работы (Out of Service)";
                case OpcDaQualityStatus.BadLastKnown: return "связь потеряна, это последнее известное значение (Last Known)";
                default: return "качество Bad без уточнения";
            }
        }

        private static bool IsPrefix(string[] folder, string[] path)
        {
            if (folder == null || path == null || folder.Length == 0 || path.Length < folder.Length) return false;
            for (int i = 0; i < folder.Length; i++)
                if (!string.Equals(folder[i], path[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static string[] SplitPath(string itemId)
        {
            var parts = itemId.Split('.');
            return parts.Length > 1 ? parts.Take(parts.Length - 1).ToArray() : new string[0];
        }

        private static string LastSegment(string itemId)
        {
            var i = itemId.LastIndexOf('.');
            return i >= 0 && i < itemId.Length - 1 ? itemId.Substring(i + 1) : itemId;
        }

        // ------------------------------------------------------------------ данные

        private void OnValuesChanged(object sender, OpcDaItemValuesChangedEventArgs args)
        {
            foreach (OpcDaItemValue v in args.Values)
            {
                if (v.Item == null) continue;
                StatusCode sc = v.Error.Failed ? (StatusCode)StatusCodes.Bad : QualityMapper.ToStatusCode(v.Quality);
                if (!v.Error.Failed && v.Quality.Master == OpcDaQualityMaster.Bad) _badItems[v.Item.ItemId] = v.Quality.Status;
                else _badItems.TryRemove(v.Item.ItemId, out _);
                var value = ValueConverter.ToUa(v.Value);
                var ts = ChooseTimestamp(v.Item.ItemId, value, v.Timestamp);
                _ua.Update(Key, v.Item.ItemId, value, sc, ts);
            }
        }

        /// <summary>
        /// Выбор метки времени по настройке timestampSource.
        /// auto: берём время DA, но если DA-сервер прислал НОВОЕ значение со СТАРЫМ временем
        /// (время не обновляется) — дальше для этого источника используем время шлюза.
        /// Время последнего изменения у редко меняющихся тегов при этом сохраняется.
        /// </summary>
        private DateTime ChooseTimestamp(string itemId, object value, DateTimeOffset daTimestamp)
        {
            var now = DateTime.UtcNow;
            if (daTimestamp == default(DateTimeOffset) || _s.TimestampSource == "gateway") return now;

            var daTs = daTimestamp.UtcDateTime;
            if (_s.TimestampSource == "da") return daTs;

            lock (_lastDa)
            {
                if (_gatewayTimeItems.Contains(itemId)) return now;
                if (value is Array) return daTs;

                // решение принимается ДЛЯ КАЖДОГО ТЕГА отдельно: один «странный» служебный тег
                // не должен лишать остальные теги настоящего времени от ПЛК
                // значение изменилось, а время DA не обновилось ИЛИ отстаёт больше чем на минуту —
                // DA-сервер ставит неверное время для этого тега
                if (_lastDa.TryGetValue(itemId, out var prev) && !Equals(prev.value, value) &&
                    (prev.daTs == daTs || Math.Abs((now - daTs).TotalSeconds) > 60))
                {
                    _gatewayTimeItems.Add(itemId);
                    _lastDa.Remove(itemId);
                    if (!_timeWarned)
                    {
                        _timeWarned = true;
                        Log.Problem(Problems.WrongTimestamps(Key, itemId));
                    }
                    return now;
                }
                _lastDa[itemId] = (value, daTs);
            }
            return daTs;
        }

        /// <summary>Запись из UA в DA (только если allowWrite=true и тег разрешает запись).</summary>
        private StatusCode Write(string itemId, object value)
        {
            try
            {
                lock (_comLock)
                {
                    if (_group == null || !_items.TryGetValue(itemId, out var item))
                        return StatusCodes.BadNotConnected;

                    var r = _group.Write(new[] { item }, new[] { value });
                    if (r[0].Failed)
                    {
                        Log.Problem(Problems.WriteRejected(Key, itemId, r[0].ToString()));
                        return StatusCodes.BadNotWritable;
                    }
                }
                Log.Info($"[{Key}] Запись {itemId} = {value}");
                return StatusCodes.Good;
            }
            catch (Exception ex)
            {
                Log.Problem(Problems.WriteRejected(Key, itemId, ex.Message));
                return StatusCodes.BadCommunicationError;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            Disconnect();
            _ua.SetAllTagsStatus(Key, StatusCodes.BadNotConnected);
            _ua.SetSourceStatus(Key, false, "Остановлено");
        }
    }
}
