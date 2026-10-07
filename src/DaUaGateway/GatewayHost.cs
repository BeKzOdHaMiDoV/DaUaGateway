using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DaUaGateway.Config;
using DaUaGateway.Discovery;
using DaUaGateway.Inbox;
using DaUaGateway.Licensing;
using DaUaGateway.Logging;
using DaUaGateway.Selection;
using DaUaGateway.Ua;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace DaUaGateway
{
    /// <summary>Сборка всех частей шлюза. Используется и в консоли, и в службе Windows.</summary>
    public sealed class GatewayHost
    {
        private GatewayConfig _cfg;
        private GatewayServer _server;
        private DiscoveryManager _discovery;
        private InboxWatcher _inbox;
        private SelectionStore _selection;
        private Timer _memoryTimer;
        private Timer _summaryTimer;

        public async Task StartAsync()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Directory.SetCurrentDirectory(baseDir); // у службы текущая папка — System32

            _cfg = GatewayConfig.Load(Path.Combine(baseDir, "gateway.json"));
            Log.Init(Path.Combine(baseDir, _cfg.Logging.Dir), _cfg.Logging.KeepDays);
            Log.Info($"DaUaGateway {GatewayInfo.Version} запускается...");

            if (!LicenseHook.Check(out var licenseMessage))
                throw new InvalidOperationException("Проверка лицензии не пройдена: " + licenseMessage);

            // ---- OPC UA сервер
            var application = new ApplicationInstance
            {
                ApplicationName = "DaUaGateway",
                ApplicationType = ApplicationType.Server
            };

            ApplicationConfiguration uaConfig;
            try
            {
            uaConfig = await application.Build(_cfg.Ua.ApplicationUri, "urn:DaUaGateway:product")
                .AsServer(new[] { _cfg.Ua.Endpoint })
                .AddUnsecurePolicyNone(_cfg.Ua.AllowUnsecured)
                .AddSignAndEncryptPolicies()
                .AddSecurityConfiguration("CN=DaUaGateway, O=DaUaGateway", Path.Combine(baseDir, "pki"))
                .SetAutoAcceptUntrustedCertificates(_cfg.Ua.AutoAcceptUntrustedCertificates)
                .CreateAsync();

            if (!await application.CheckApplicationInstanceCertificatesAsync(false))
                throw new InvalidOperationException("Не удалось создать/загрузить сертификат приложения");

            _server = new GatewayServer(_cfg.Ua.NamespaceUri);
            await application.StartAsync(_server);
            }
            catch (Exception ex)
            {
                var problem = Problems.UaStartFailed(_cfg.Ua.Endpoint, ex);
                Log.Problem(problem, error: true);
                throw new InvalidOperationException(problem.ToString(), ex);
            }

            // E402: SCADA подключается с шифрованием, но её сертификат не доверен
            var reportedCerts = new System.Collections.Generic.HashSet<string>();
            uaConfig.CertificateValidator.CertificateValidation += (sender, e) =>
            {
                if (_cfg.Ua.AutoAcceptUntrustedCertificates || e.Accept || e.Certificate == null) return;
                lock (reportedCerts)
                    if (!reportedCerts.Add(e.Certificate.Thumbprint)) return;
                Log.Problem(Problems.ClientCertificateRejected(e.Certificate.Subject, Path.Combine(baseDir, "pki", "rejected", "certs")));
            };
            Log.Info($"UA-сервер запущен: {_cfg.Ua.Endpoint}" +
                     (_cfg.Ua.AllowUnsecured ? "  (ВНИМАНИЕ: разрешён режим без шифрования)" : ""));

            // ---- источники DA
            var manual = new ManualTagStore(Path.Combine(baseDir, _cfg.DataDir, "manual-tags.json"));

            _selection = new SelectionStore(Path.Combine(baseDir, _cfg.DataDir, "selection.json"));
            _selection.Start();

            _discovery = new DiscoveryManager(_cfg, _server.NodeManager, manual, _selection);
            _discovery.Start();

            _inbox = new InboxWatcher(Path.Combine(baseDir, _cfg.InboxDir), manual);
            _inbox.Start();

            // ---- контроль памяти (для поиска утечек в длительной работе)
            _memoryTimer = new Timer(_ => LogMemory(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

            // сводка «какие DA-серверы переведены в UA» — через 20 с после старта и дальше раз в 10 минут
            _summaryTimer = new Timer(_ => PrintSummary(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10));

            Log.Info("Шлюз запущен");
        }

        // ------------------------------------------------------------------ для окна-пульта

        public string Endpoint => (_cfg?.Ua.Endpoint ?? "opc.tcp://localhost:4840/DaUaGateway").Replace("localhost", Environment.MachineName);

        public string LogsDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _cfg?.Logging.Dir ?? "logs");

        /// <summary>Строки состояния по DA-серверам и итоги (для окна-пульта).</summary>
        public (List<string> lines, int sources, int tags) Snapshot()
        {
            var lines = new List<string>();
            int tags = 0, sources = 0;
            var d = _discovery;
            if (d == null) return (lines, 0, 0);

            foreach (var s in d.Sessions)
            {
                sources++;
                string state;
                if (s.Connected && s.TagCount == 0 && s.RejectedCount == 0) state = "подключён, теги не выбраны";
                else if (s.Connected) state = "переведено тегов: " + s.TagCount + (s.RejectedCount > 0 ? ", отклонено: " + s.RejectedCount : "");
                else if (string.IsNullOrEmpty(s.LastError)) state = "подключение…";
                else state = "нет связи: " + s.LastError;
                if (s.Connected) tags += s.TagCount;
                lines.Add(s.Key + "  —  " + state);
            }
            foreach (var key in d.IdleServers)
                lines.Add(key + "  —  теги не выбраны (не подключён)");
            return (lines, sources, tags);
        }

        private void PrintSummary()
        {
            try
            {
                var sessions = _discovery?.Sessions;
                if (sessions == null) return;
                var endpoint = _cfg.Ua.Endpoint.Replace("localhost", Environment.MachineName);

                Log.Info("┌──────────── DA → OPC UA: сводка ────────────");
                var idle = _discovery.IdleServers;
                if (sessions.Count == 0 && idle.Count == 0)
                    Log.Info("│ DA-серверы пока не найдены");
                foreach (var s in sessions)
                {
                    string state;
                    if (s.Connected && s.TagCount == 0 && s.RejectedCount == 0)
                        state = "[ПОДКЛЮЧЁН]  теги не выбраны — откройте Конфигуратор";
                    else if (s.Connected)
                        state = $"[OK] переведён   тегов: {s.TagCount}" + (s.RejectedCount > 0 ? $", отклонено: {s.RejectedCount}" : "");
                    else if (string.IsNullOrEmpty(s.LastError))
                        state = "[...] подключение";
                    else
                        state = $"[НЕТ СВЯЗИ]  ({s.LastError})";
                    Log.Info($"│ {s.Key,-42} {state}");
                }
                foreach (var key in idle)
                    Log.Info($"│ {key,-42} [НЕ ВЫБРАН]  теги не выбраны — не подключён");
                Log.Info($"│ UA-сервер для SCADA: {endpoint}");
                Log.Info("│ Список тегов: папка data\\tag-map  или команда  DaUaGateway.exe --list");
                Log.Info("└──────────────────────────────────────────────");
            }
            catch { }
        }

        private void LogMemory()
        {
            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    Log.Info($"Память: {p.PrivateMemorySize64 / 1048576} МБ (управляемая {GC.GetTotalMemory(false) / 1048576} МБ), " +
                             $"дескрипторов {p.HandleCount}, потоков {p.Threads.Count}, источников {_discovery?.SourceCount}");
                }
            }
            catch { }
        }

        public async Task StopAsync()
        {
            Log.Info("Остановка шлюза...");
            _memoryTimer?.Dispose();
            _summaryTimer?.Dispose();
            _inbox?.Dispose();
            _discovery?.Dispose();
            _selection?.Dispose();
            if (_server != null) await _server.StopAsync();
            Log.Info("Шлюз остановлен");
        }
    }
}
