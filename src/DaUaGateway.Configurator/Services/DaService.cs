using System;
using System.Collections.Generic;
using System.Linq;
using TitaniumAS.Opc.Client.Common;
using TitaniumAS.Opc.Client.Da;
using TitaniumAS.Opc.Client.Da.Browsing;

namespace DaUaGateway.Configurator.Services
{
    public sealed class DaElement
    {
        public string Name;
        public string ItemId;
        public bool IsItem;
        public bool HasChildren;
    }

    public sealed class DaReadResult
    {
        public object Value;
        public string Quality;
        public DateTimeOffset Timestamp;
        public string Type;
        public string Access;
        public string Error;
    }

    /// <summary>Работа с DA-сервером: список серверов, обзор дерева, чтение значения. Все вызовы — последовательно.</summary>
    public sealed class DaService : IDisposable
    {
        private readonly object _sync = new object();
        private OpcDaServer _server;
        private OpcDaBrowserAuto _browser;
        private OpcDaGroup _readGroup;

        public string ProgId { get; private set; }

        public static List<string> EnumerateServers()
        {
            var found = new OpcServerEnumeratorAuto().Enumerate("localhost", OpcServerCategory.OpcDaServers);
            return found.Select(d => d.ProgId).Where(p => !string.IsNullOrEmpty(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        }

        public void Connect(string progId)
        {
            lock (_sync)
            {
                DisconnectInternal();
                var server = new OpcDaServer(UrlBuilder.Build(progId));
                server.Connect();
                _server = server;
                _browser = new OpcDaBrowserAuto(server);
                ProgId = progId;
            }
        }

        public List<DaElement> Browse(string parentItemId)
        {
            lock (_sync)
            {
                if (_browser == null) return new List<DaElement>();
                return _browser.GetElements(parentItemId ?? "")
                    .Where(e => !e.IsHint)
                    .Select(e => new DaElement
                    {
                        Name = string.IsNullOrEmpty(e.Name) ? e.ItemId : e.Name,
                        ItemId = e.ItemId,
                        IsItem = e.IsItem,
                        HasChildren = e.HasChildren
                    })
                    .OrderBy(e => e.HasChildren ? 0 : 1)
                    .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>Однократное чтение тега — чтобы разработчик видел значение, тип и права при выборе.</summary>
        public DaReadResult Read(string itemId)
        {
            lock (_sync)
            {
                if (_server == null) return new DaReadResult { Error = "нет подключения" };
                if (_readGroup == null)
                {
                    _readGroup = _server.AddGroup("configurator-read");
                    _readGroup.IsActive = true;
                }

                var added = _readGroup.AddItems(new[] { new OpcDaItemDefinition { ItemId = itemId, IsActive = true } });
                if (added[0].Error.Failed || added[0].Item == null)
                    return new DaReadResult { Error = "DA-сервер не принял тег: " + added[0].Error };

                var item = added[0].Item;
                try
                {
                    var v = _readGroup.Read(new[] { item }, OpcDaDataSource.Device)[0];
                    return new DaReadResult
                    {
                        Value = v.Value,
                        Quality = v.Quality.Master.ToString(),
                        Timestamp = v.Timestamp,
                        Type = item.CanonicalDataType?.Name ?? "",
                        Access = item.AccessRights.ToString(),
                        Error = v.Error.Failed ? v.Error.ToString() : null
                    };
                }
                finally
                {
                    try { _readGroup.RemoveItems(new[] { item }); } catch { }
                }
            }
        }

        private void DisconnectInternal()
        {
            try { _server?.Dispose(); } catch { }
            _server = null;
            _browser = null;
            _readGroup = null;
            ProgId = null;
        }

        public void Dispose()
        {
            lock (_sync) DisconnectInternal();
        }
    }
}
