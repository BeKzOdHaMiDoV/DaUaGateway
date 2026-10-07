using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace DaUaGateway.Configurator.Services
{
    public sealed class UaVariable
    {
        public NodeId NodeId;
        public string Name;
        public string Path;
    }

    /// <summary>
    /// Клиент OPC UA к конвертеру на этом компьютере: живые значения переведённых тегов,
    /// версия дерева (ModelVersion) и папка, из которой запущен конвертер (InstallDir).
    /// Сам переподключается, если конвертер перезапустили.
    /// </summary>
    public sealed class UaMonitor : IDisposable
    {
        public const string NamespaceUri = "urn:DaUaGateway:tags";

        private readonly ITelemetryContext _telemetry = DefaultTelemetry.Create(b => { });
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<NodeId, MonitoredItem> _items = new Dictionary<NodeId, MonitoredItem>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private ApplicationConfiguration _config;
        private ISession _session;
        private Subscription _subscription;
        private string _url;
        private volatile bool _connected;

        public event Action<bool, string> StateChanged;
        public event Action<NodeId, DataValue> ValueChanged;
        public event Action<uint> ModelVersionChanged;

        public bool Connected => _connected;
        public ushort NsIndex { get; private set; }
        public string GatewayInstallDir { get; private set; }
        public string GatewayVersion { get; private set; }
        public bool Secure { get; private set; }

        private NodeId ModelVersionId => new NodeId("Gateway|ModelVersion", NsIndex);

        // ------------------------------------------------------------------ запуск

        public async Task InitAsync(string pkiDir)
        {
            var app = new ApplicationInstance(_telemetry)
            {
                ApplicationName = "DaUaGateway Configurator",
                ApplicationType = ApplicationType.Client
            };
            _config = await app.Build("urn:DaUaGateway:Configurator", "urn:DaUaGateway:product")
                .AsClient()
                .AddSecurityConfiguration("CN=DaUaGateway Configurator, O=DaUaGateway", pkiDir)
                .SetAutoAcceptUntrustedCertificates(true) // подключаемся только к своему конвертеру на этом ПК
                .CreateAsync();
            await app.CheckApplicationInstanceCertificatesAsync(false);
        }

        /// <summary>
        /// Положить сертификат Конфигуратора в доверенные у конвертера (та же папка, тот же ПК) —
        /// чтобы подключение с шифрованием работало и в рабочем режиме конвертера.
        /// </summary>
        public string TrustSelfInGateway(string pkiDir, string gatewayDir)
        {
            var own = Path.Combine(pkiDir, "own", "certs");
            if (!Directory.Exists(own)) return null;
            var cert = Directory.GetFiles(own, "*.der").FirstOrDefault();
            if (cert == null) return null;

            var trusted = Path.Combine(gatewayDir, "pki", "trusted", "certs");
            Directory.CreateDirectory(trusted);
            var target = Path.Combine(trusted, Path.GetFileName(cert));
            if (!File.Exists(target)) File.Copy(cert, target);
            return target;
        }

        public void Start(string url)
        {
            _url = url;
            Task.Run(() => LoopAsync(_cts.Token));
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_connected)
                {
                    try { await ConnectAsync(); }
                    catch (Exception ex)
                    {
                        StateChanged?.Invoke(false, "Конвертер не отвечает: " + ex.Message);
                        await CloseAsync();
                    }
                }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task ConnectAsync()
        {
            ISession session = null;
            Exception last = null;

            // сначала с шифрованием, если конвертер не пустил — без (тестовый режим конвертера)
            foreach (var secure in new[] { true, false })
            {
                try
                {
                    var description = await CoreClientUtils.SelectEndpointAsync(_config, _url, secure, _telemetry);
                    var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(_config));
                    session = await new DefaultSessionFactory(_telemetry).CreateAsync(
                        _config, endpoint, false, false, "DaUaGateway Configurator", 60000, new UserIdentity(), null);
                    Secure = description.SecurityMode != MessageSecurityMode.None;
                    break;
                }
                catch (Exception ex) { last = ex; }
            }
            if (session == null) throw last ?? new InvalidOperationException("нет подходящей точки подключения");

            session.KeepAlive += OnKeepAlive;

            await _lock.WaitAsync();
            try
            {
                _session = session;
                int ns = session.NamespaceUris.GetIndex(NamespaceUri);
                NsIndex = ns > 0 ? (ushort)ns : (ushort)0;
                if (NsIndex == 0) throw new InvalidOperationException("по этому адресу работает не DaUaGateway");

                GatewayInstallDir = await ReadStringAsync("Gateway|InstallDir");
                GatewayVersion = await ReadStringAsync("Gateway|Version");

                _subscription = new Subscription(_telemetry)
                {
                    DisplayName = "Configurator",
                    PublishingInterval = 500,
                    PublishingEnabled = true,
                    KeepAliveCount = 10,
                    LifetimeCount = 100
                };
                session.AddSubscription(_subscription);
                await _subscription.CreateAsync();

                _items.Clear();
                AddItem(ModelVersionId);
                await _subscription.ApplyChangesAsync();
            }
            finally { _lock.Release(); }

            _connected = true;
            StateChanged?.Invoke(true, "Подключено к конвертеру " + GatewayVersion + (Secure ? " (с шифрованием)" : " (без шифрования)"));
        }

        private async Task<string> ReadStringAsync(string id)
        {
            try
            {
                var dv = await _session.ReadValueAsync(new NodeId(id, NsIndex));
                return dv?.Value as string;
            }
            catch { return null; } // старая версия конвертера без этой переменной
        }

        private void OnKeepAlive(ISession s, KeepAliveEventArgs e)
        {
            if (e.Status == null || !ServiceResult.IsBad(e.Status) || !_connected) return;
            _connected = false;
            StateChanged?.Invoke(false, "Связь с конвертером потеряна, переподключение…");
            Task.Run(CloseAsync);
        }

        private async Task CloseAsync()
        {
            ISession s;
            await _lock.WaitAsync();
            try
            {
                s = _session;
                _session = null;
                _subscription = null;
                _items.Clear();
            }
            finally { _lock.Release(); }

            if (s == null) return;
            s.KeepAlive -= OnKeepAlive;
            try { await s.CloseAsync(2000, true); } catch { }
            try { (s as IDisposable)?.Dispose(); } catch { }
        }

        // ------------------------------------------------------------------ обзор и подписки

        /// <summary>Все переменные под папкой источника Objects/Servers/&lt;progId&gt; (рекурсивно).</summary>
        public async Task<List<UaVariable>> BrowseSourceAsync(string progId)
        {
            var result = new List<UaVariable>();
            var session = _session;
            if (session == null || !_connected) return result;
            try
            {
                await BrowseRecursiveAsync(session, new NodeId(progId, NsIndex), "", result, 0);
            }
            catch (ServiceResultException)
            {
                // папки сервера в UA нет: теги не выбраны, конвертер к этому DA-серверу не подключён
                result.Clear();
            }
            return result;
        }

        private async Task BrowseRecursiveAsync(ISession session, NodeId parent, string path, List<UaVariable> result, int depth)
        {
            if (depth > 20) return;
            var browser = new Browser(session, new BrowserOptions
            {
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (int)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            });

            var refs = await browser.BrowseAsync(parent);
            foreach (var r in refs)
            {
                var id = ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris);
                if (NodeId.IsNull(id)) continue;
                var name = r.DisplayName?.Text ?? r.BrowseName?.Name ?? id.ToString();
                if (r.NodeClass == NodeClass.Variable)
                    result.Add(new UaVariable { NodeId = id, Name = name, Path = path });
                else
                    await BrowseRecursiveAsync(session, id, path.Length == 0 ? name : path + " / " + name, result, depth + 1);
            }
        }

        /// <summary>Подписаться ровно на этот набор переменных (ModelVersion остаётся всегда).</summary>
        public async Task SetMonitoredAsync(IEnumerable<NodeId> ids)
        {
            await _lock.WaitAsync();
            try
            {
                if (_subscription == null) return;
                var wanted = new HashSet<NodeId>(ids) { ModelVersionId };
                bool changed = false;

                foreach (var id in _items.Keys.Where(k => !wanted.Contains(k)).ToList())
                {
                    _items[id].Notification -= OnNotification;
                    _subscription.RemoveItem(_items[id]);
                    _items.Remove(id);
                    changed = true;
                }
                foreach (var id in wanted.Where(w => !_items.ContainsKey(w)))
                {
                    AddItem(id);
                    changed = true;
                }
                if (changed) await _subscription.ApplyChangesAsync();
            }
            catch
            {
                // связь пропала — подписки создадутся заново после переподключения
            }
            finally { _lock.Release(); }
        }

        private void AddItem(NodeId id)
        {
            var item = new MonitoredItem(_telemetry)
            {
                StartNodeId = id,
                AttributeId = Attributes.Value,
                SamplingInterval = 250,
                QueueSize = 1,
                DiscardOldest = true,
                DisplayName = id.ToString()
            };
            item.Notification += OnNotification;
            _subscription.AddItem(item);
            _items[id] = item;
        }

        private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
        {
            if (!(e.NotificationValue is MonitoredItemNotification n) || n.Value == null) return;
            if (item.StartNodeId == ModelVersionId)
            {
                if (StatusCode.IsGood(n.Value.StatusCode) && n.Value.Value != null)
                    ModelVersionChanged?.Invoke(Convert.ToUInt32(n.Value.Value));
                return;
            }
            ValueChanged?.Invoke(item.StartNodeId, n.Value);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { CloseAsync().Wait(3000); } catch { }
        }
    }
}
