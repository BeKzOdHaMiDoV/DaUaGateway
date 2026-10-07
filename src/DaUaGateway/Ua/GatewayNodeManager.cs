using System;
using System.Collections.Generic;
using System.Linq;
using DaUaGateway.Da;
using Opc.Ua;
using Opc.Ua.Server;

namespace DaUaGateway.Ua
{
    /// <summary>Запись из UA в DA. Возвращает статус операции.</summary>
    public delegate StatusCode TagWriteHandler(string itemId, object value);

    /// <summary>
    /// Динамическое адресное пространство:
    ///
    ///   Objects/
    ///     Gateway/          Version, StartTime, ModelVersion, SourceCount
    ///     Servers/
    ///       &lt;источник&gt;/     _Status, _Connected, папки и теги как в DA-сервере
    ///
    /// NodeId тега:   ns=&lt;urn:DaUaGateway:tags&gt;;s=&lt;источник&gt;|&lt;ItemID&gt;
    /// ModelVersion увеличивается при любом изменении дерева — клиенты (дашборд)
    /// подписываются на него и перечитывают дерево, когда оно меняется.
    /// </summary>
    public class GatewayNodeManager : CustomNodeManager2
    {
        private sealed class SourceNodes
        {
            public FolderState Folder;
            public BaseDataVariableState Status;
            public BaseDataVariableState Connected;
            public readonly Dictionary<string, FolderState> Folders = new Dictionary<string, FolderState>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> TagFolder = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, BaseDataVariableState> Tags = new Dictionary<string, BaseDataVariableState>(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, SourceNodes> _sources =
            new Dictionary<string, SourceNodes>(StringComparer.OrdinalIgnoreCase);

        private FolderState _serversRoot;
        private BaseDataVariableState _modelVersion;
        private BaseDataVariableState _sourceCount;
        private uint _modelVersionValue;

        public GatewayNodeManager(IServerInternal server, ApplicationConfiguration configuration, string namespaceUri)
            : base(server, configuration, namespaceUri)
        {
            SystemContext.NodeIdFactory = this;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
                    externalReferences[ObjectIds.ObjectsFolder] = refs = new List<IReference>();

                var gw = NewFolder(null, "Gateway", "Gateway");
                gw.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                refs.Add(new NodeStateReference(ReferenceTypes.Organizes, false, gw.NodeId));

                SetGood(NewVar(gw, "Gateway|Version", "Version", DataTypeIds.String, ValueRanks.Scalar, false), GatewayInfo.Version);
                SetGood(NewVar(gw, "Gateway|StartTime", "StartTime", DataTypeIds.DateTime, ValueRanks.Scalar, false), DateTime.UtcNow);
                // папка, из которой запущен конвертер — Конфигуратор сверяет её со своей
                SetGood(NewVar(gw, "Gateway|InstallDir", "InstallDir", DataTypeIds.String, ValueRanks.Scalar, false),
                    AppDomain.CurrentDomain.BaseDirectory);
                _modelVersion = NewVar(gw, "Gateway|ModelVersion", "ModelVersion", DataTypeIds.UInt32, ValueRanks.Scalar, false);
                SetGood(_modelVersion, 0u);
                _sourceCount = NewVar(gw, "Gateway|SourceCount", "SourceCount", DataTypeIds.Int32, ValueRanks.Scalar, false);
                SetGood(_sourceCount, 0);

                _serversRoot = NewFolder(null, "Servers", "Servers");
                _serversRoot.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                refs.Add(new NodeStateReference(ReferenceTypes.Organizes, false, _serversRoot.NodeId));

                AddPredefinedNode(SystemContext, gw);
                AddPredefinedNode(SystemContext, _serversRoot);
            }
        }

        // ------------------------------------------------------------------ источники

        public void EnsureSource(string key, string displayName)
        {
            lock (Lock)
            {
                if (_sources.ContainsKey(key)) return;

                var sn = new SourceNodes { Folder = NewFolder(_serversRoot, key, displayName) };
                sn.Status = NewVar(sn.Folder, key + "|$Status", "_Status", DataTypeIds.String, ValueRanks.Scalar, false);
                sn.Connected = NewVar(sn.Folder, key + "|$Connected", "_Connected", DataTypeIds.Boolean, ValueRanks.Scalar, false);
                SetGood(sn.Status, "Подключение...");
                SetGood(sn.Connected, false);

                AddPredefinedNode(SystemContext, sn.Folder);
                _sources[key] = sn;

                SetGood(_sourceCount, _sources.Count);
                BumpModelVersion();
            }
        }

        /// <summary>Убрать источник целиком из UA (выбор снят — конвертер отключился от DA-сервера).</summary>
        public void RemoveSource(string key)
        {
            lock (Lock)
            {
                if (!_sources.TryGetValue(key, out var sn)) return;
                foreach (var v in sn.Tags.Values.ToList()) DeleteNode(SystemContext, v.NodeId);
                foreach (var f in sn.Folders.OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).ToList())
                    DeleteNode(SystemContext, f.NodeId);
                DeleteNode(SystemContext, sn.Status.NodeId);
                DeleteNode(SystemContext, sn.Connected.NodeId);
                DeleteNode(SystemContext, sn.Folder.NodeId);
                _sources.Remove(key);
                SetGood(_sourceCount, _sources.Count);
                BumpModelVersion();
            }
        }

        public void SetSourceStatus(string key, bool connected, string text)
        {
            lock (Lock)
            {
                if (!_sources.TryGetValue(key, out var sn)) return;
                SetGood(sn.Status, text);
                SetGood(sn.Connected, connected);
            }
        }

        // ------------------------------------------------------------------ теги

        /// <summary>Приводит набор тегов источника к переданному списку: добавляет новые, удаляет исчезнувшие.</summary>
        public (int added, int removed) SyncTags(string key, IList<TagInfo> tags, TagWriteHandler writer)
        {
            lock (Lock)
            {
                if (!_sources.TryGetValue(key, out var sn)) return (0, 0);

                var wanted = new HashSet<string>(tags.Select(t => t.ItemId), StringComparer.Ordinal);
                int added = 0, removed = 0;

                foreach (var id in sn.Tags.Keys.Where(id => !wanted.Contains(id)).ToList())
                {
                    DeleteNode(SystemContext, sn.Tags[id].NodeId);
                    sn.Tags.Remove(id);
                    sn.TagFolder.Remove(id);
                    removed++;
                }

                foreach (var t in tags)
                {
                    if (sn.Tags.TryGetValue(t.ItemId, out var existing))
                    {
                        // тег был отвергнут DA, а теперь принят (или наоборот) — обновим статус
                        if (t.Failed && existing.StatusCode != StatusCodes.BadNodeIdUnknown)
                            SetStatus(existing, StatusCodes.BadNodeIdUnknown);
                        continue;
                    }

                    var parent = EnsureFolderPath(sn, key, t.Path);
                    var v = NewVar(parent, key + "|" + t.ItemId, t.Name,
                        t.DataType ?? DataTypeIds.BaseDataType, t.ValueRank, t.Writable && writer != null);
                    v.Description = new LocalizedText(t.ItemId); // исходный ItemID из DA
                    v.Value = null;
                    v.StatusCode = t.Failed ? StatusCodes.BadNodeIdUnknown : StatusCodes.BadWaitingForInitialData;
                    v.Timestamp = DateTime.UtcNow;

                    if (t.Writable && writer != null)
                    {
                        var itemId = t.ItemId;
                        v.OnSimpleWriteValue = (ISystemContext c, NodeState n, ref object value) =>
                            new ServiceResult(writer(itemId, value));
                    }

                    AddPredefinedNode(SystemContext, v);
                    sn.Tags[t.ItemId] = v;
                    sn.TagFolder[t.ItemId] = FolderKey(t.Path);
                    added++;
                }

                if (removed > 0) PruneEmptyFolders(sn);
                if (added > 0 || removed > 0) BumpModelVersion();
                return (added, removed);
            }
        }

        public void Update(string key, string itemId, object value, StatusCode status, DateTime sourceTimestampUtc)
        {
            lock (Lock)
            {
                if (!_sources.TryGetValue(key, out var sn) || !sn.Tags.TryGetValue(itemId, out var v)) return;
                v.Value = value;
                v.StatusCode = status;
                v.Timestamp = sourceTimestampUtc;
                v.ClearChangeMasks(SystemContext, false);
            }
        }

        public void SetAllTagsStatus(string key, StatusCode status)
        {
            lock (Lock)
            {
                if (!_sources.TryGetValue(key, out var sn)) return;
                foreach (var v in sn.Tags.Values) SetStatus(v, status);
            }
        }

        // ------------------------------------------------------------------ помощники

        private static string FolderKey(string[] path)
        {
            var key = "";
            if (path != null)
                foreach (var segment in path)
                    if (!string.IsNullOrEmpty(segment)) key += "/" + segment;
            return key;
        }

        /// <summary>Удаляет папки, в которых не осталось ни одного тега (после снятия выбора в Конфигураторе).</summary>
        private void PruneEmptyFolders(SourceNodes sn)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folderKey in sn.TagFolder.Values)
            {
                var k = folderKey;
                while (!string.IsNullOrEmpty(k))
                {
                    if (!used.Add(k)) break;
                    k = k.Substring(0, k.LastIndexOf('/'));
                }
            }
            foreach (var key in sn.Folders.Keys.Where(k => !used.Contains(k)).OrderByDescending(k => k.Length).ToList())
            {
                DeleteNode(SystemContext, sn.Folders[key].NodeId);
                sn.Folders.Remove(key);
            }
        }

        private FolderState EnsureFolderPath(SourceNodes sn, string key, string[] path)
        {
            FolderState parent = sn.Folder;
            if (path == null) return parent;

            var current = "";
            foreach (var segment in path)
            {
                if (string.IsNullOrEmpty(segment)) continue;
                current += "/" + segment;
                if (!sn.Folders.TryGetValue(current, out var folder))
                {
                    folder = NewFolder(parent, key + "|" + current, segment);
                    AddPredefinedNode(SystemContext, folder);
                    sn.Folders[current] = folder;
                }
                parent = folder;
            }
            return parent;
        }

        private FolderState NewFolder(NodeState parent, string id, string name)
        {
            var f = new FolderState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(id, NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = new LocalizedText(name),
                EventNotifier = EventNotifiers.None
            };
            parent?.AddChild(f);
            return f;
        }

        private BaseDataVariableState NewVar(NodeState parent, string id, string name,
            NodeId dataType, int valueRank, bool writable)
        {
            var access = writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead;
            var v = new BaseDataVariableState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId(id, NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = new LocalizedText(name),
                DataType = dataType,
                ValueRank = valueRank,
                AccessLevel = access,
                UserAccessLevel = access,
                Historizing = false,
                StatusCode = StatusCodes.BadWaitingForInitialData,
                Timestamp = DateTime.UtcNow
            };
            parent?.AddChild(v);
            return v;
        }

        private void SetGood(BaseDataVariableState v, object value)
        {
            v.Value = value;
            v.StatusCode = StatusCodes.Good;
            v.Timestamp = DateTime.UtcNow;
            v.ClearChangeMasks(SystemContext, false);
        }

        private void SetStatus(BaseDataVariableState v, StatusCode status)
        {
            v.StatusCode = status;
            v.Timestamp = DateTime.UtcNow;
            v.ClearChangeMasks(SystemContext, false);
        }

        private void BumpModelVersion()
        {
            _modelVersionValue++;
            SetGood(_modelVersion, _modelVersionValue);
        }
    }
}
