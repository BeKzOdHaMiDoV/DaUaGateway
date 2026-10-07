using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace DaUaGateway.Config
{
    public class GatewayConfig
    {
        [JsonProperty("discovery")] public DiscoveryConfig Discovery { get; set; } = new DiscoveryConfig();
        [JsonProperty("defaults")] public SourceDefaults Defaults { get; set; } = new SourceDefaults();
        [JsonProperty("servers")] public List<ServerRule> Servers { get; set; } = new List<ServerRule>();
        [JsonProperty("inboxDir")] public string InboxDir { get; set; } = "inbox";
        [JsonProperty("dataDir")] public string DataDir { get; set; } = "data";
        [JsonProperty("ua")] public UaSettings Ua { get; set; } = new UaSettings();
        [JsonProperty("logging")] public LogSettings Logging { get; set; } = new LogSettings();

        public static GatewayConfig Load(string path) =>
            JsonConvert.DeserializeObject<GatewayConfig>(File.ReadAllText(path)) ?? new GatewayConfig();

        /// <summary>Правило из конфига для сервера (или null, если сервер найден автопоиском и не описан).</summary>
        public ServerRule FindRule(string host, string progId) =>
            Servers.FirstOrDefault(s =>
                string.Equals(s.ProgId, progId, StringComparison.OrdinalIgnoreCase) &&
                SourceKey.SameHost(s.Host, host));
    }

    public class DiscoveryConfig
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("hosts")] public List<string> Hosts { get; set; } = new List<string> { "localhost" };
        [JsonProperty("scanIntervalSec")] public int ScanIntervalSec { get; set; } = 30;
        [JsonProperty("rebrowseIntervalSec")] public int RebrowseIntervalSec { get; set; } = 300;
        [JsonProperty("excludeProgIds")] public List<string> ExcludeProgIds { get; set; } = new List<string>();
    }

    public class SourceDefaults
    {
        [JsonProperty("mode")] public string Mode { get; set; } = "auto";
        [JsonProperty("updateRateMs")] public int UpdateRateMs { get; set; } = 500;
        [JsonProperty("include")] public List<string> Include { get; set; } = new List<string> { "*" };
        [JsonProperty("exclude")] public List<string> Exclude { get; set; } = new List<string>();
        [JsonProperty("allowWrite")] public bool AllowWrite { get; set; }
        [JsonProperty("maxTags")] public int MaxTags { get; set; } = 20000;
        [JsonProperty("reconnectDelayMs")] public int ReconnectDelayMs { get; set; } = 5000;
        [JsonProperty("healthCheckMs")] public int HealthCheckMs { get; set; } = 2000;
        // Откуда брать время значения: "da" — из DA-сервера, "gateway" — время получения шлюзом,
        // "auto" — из DA, но если DA-сервер меняет значения, не обновляя время, — время шлюза
        [JsonProperty("timestampSource")] public string TimestampSource { get; set; } = "auto";
    }

    /// <summary>Переопределения для конкретного DA-сервера. Пустые поля берутся из defaults.</summary>
    public class ServerRule
    {
        [JsonProperty("progId")] public string ProgId { get; set; }
        [JsonProperty("host")] public string Host { get; set; } = "localhost";
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("displayName")] public string DisplayName { get; set; }
        [JsonProperty("mode")] public string Mode { get; set; }
        [JsonProperty("updateRateMs")] public int? UpdateRateMs { get; set; }
        [JsonProperty("include")] public List<string> Include { get; set; }
        [JsonProperty("exclude")] public List<string> Exclude { get; set; }
        [JsonProperty("tags")] public List<string> Tags { get; set; }
        [JsonProperty("allowWrite")] public bool? AllowWrite { get; set; }
        [JsonProperty("timestampSource")] public string TimestampSource { get; set; }
    }

    /// <summary>Итоговые настройки источника (defaults + правило).</summary>
    public class SourceSettings
    {
        public string Host;
        public string ProgId;
        public string Key;
        public string DisplayName;
        public bool AutoMode;
        public int UpdateRateMs;
        public List<string> Include;
        public List<string> Exclude;
        public List<string> Tags;
        public bool AllowWrite;
        public int MaxTags;
        public int ReconnectDelayMs;
        public int HealthCheckMs;
        public int RebrowseIntervalSec;
        public string TimestampSource;
        public string MapDir;          // куда писать список тегов DA → UA
        public string Endpoint;        // адрес UA-сервера шлюза
        public string NamespaceUri;

        public static SourceSettings Build(GatewayConfig cfg, string host, string progId, ServerRule rule)
        {
            var d = cfg.Defaults;
            return new SourceSettings
            {
                Host = host,
                ProgId = progId,
                Key = SourceKey.Make(host, progId),
                DisplayName = rule?.DisplayName ?? SourceKey.Make(host, progId),
                AutoMode = !string.Equals(rule?.Mode ?? d.Mode, "manual", StringComparison.OrdinalIgnoreCase),
                UpdateRateMs = rule?.UpdateRateMs ?? d.UpdateRateMs,
                Include = rule?.Include ?? d.Include ?? new List<string> { "*" },
                Exclude = (d.Exclude ?? new List<string>()).Concat(rule?.Exclude ?? new List<string>()).ToList(),
                Tags = rule?.Tags ?? new List<string>(),
                AllowWrite = rule?.AllowWrite ?? d.AllowWrite,
                MaxTags = d.MaxTags,
                ReconnectDelayMs = d.ReconnectDelayMs,
                HealthCheckMs = d.HealthCheckMs,
                RebrowseIntervalSec = cfg.Discovery.RebrowseIntervalSec,
                MapDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, cfg.DataDir, "tag-map"),
                Endpoint = cfg.Ua.Endpoint,
                NamespaceUri = cfg.Ua.NamespaceUri,
                TimestampSource = (rule?.TimestampSource ?? d.TimestampSource ?? "auto").Trim().ToLowerInvariant()
            };
        }
    }

    public class UaSettings
    {
        [JsonProperty("endpoint")] public string Endpoint { get; set; } = "opc.tcp://localhost:4840/DaUaGateway";
        [JsonProperty("applicationUri")] public string ApplicationUri { get; set; } = "urn:DaUaGateway";
        [JsonProperty("namespaceUri")] public string NamespaceUri { get; set; } = "urn:DaUaGateway:tags";
        // Для релиза: allowUnsecured=false, autoAcceptUntrustedCertificates=false
        [JsonProperty("allowUnsecured")] public bool AllowUnsecured { get; set; } = true;
        [JsonProperty("autoAcceptUntrustedCertificates")] public bool AutoAcceptUntrustedCertificates { get; set; } = true;
    }

    public class LogSettings
    {
        [JsonProperty("dir")] public string Dir { get; set; } = "logs";
        [JsonProperty("keepDays")] public int KeepDays { get; set; } = 14;
    }

    /// <summary>Ключ источника: "ProgId" для локального сервера, "host/ProgId" для удалённого.</summary>
    public static class SourceKey
    {
        public static bool IsLocal(string host) =>
            string.IsNullOrWhiteSpace(host) || host == "." ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host == "127.0.0.1" ||
            host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        public static bool SameHost(string a, string b) =>
            (IsLocal(a) && IsLocal(b)) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public static string Make(string host, string progId) =>
            IsLocal(host) ? progId : host + "/" + progId;
    }
}
