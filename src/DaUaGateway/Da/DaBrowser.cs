using System;
using System.Collections.Generic;
using System.Linq;
using DaUaGateway.Logging;
using TitaniumAS.Opc.Client.Da;
using TitaniumAS.Opc.Client.Da.Browsing;

namespace DaUaGateway.Da
{
    /// <summary>Рекурсивный обзор всего дерева тегов DA-сервера (DA 3.0 / 2.0 / 1.0 — автоматически).</summary>
    public static class DaBrowser
    {
        public sealed class BrowsedItem
        {
            public string ItemId;
            public string[] Path;
            public string Name;
        }

        public static List<BrowsedItem> BrowseAll(OpcDaServer server, int maxItems, int maxDepth = 16)
        {
            var result = new List<BrowsedItem>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var browser = new OpcDaBrowserAuto(server);
            Recurse(browser, "", new string[0], 0, maxItems, maxDepth, result, visited);
            return result;
        }

        private static void Recurse(OpcDaBrowserAuto browser, string parentId, string[] path, int depth,
            int maxItems, int maxDepth, List<BrowsedItem> result, HashSet<string> visited)
        {
            if (depth > maxDepth || result.Count >= maxItems) return;

            OpcDaBrowseElement[] elements;
            try
            {
                elements = browser.GetElements(parentId);
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось прочитать ветку '{parentId}': {ex.Message}");
                return;
            }

            foreach (var e in elements)
            {
                if (result.Count >= maxItems) return;
                if (e.IsHint) continue;

                if (e.IsItem && !string.IsNullOrEmpty(e.ItemId) && visited.Add(e.ItemId))
                {
                    result.Add(new BrowsedItem
                    {
                        ItemId = e.ItemId,
                        Path = path,
                        Name = string.IsNullOrEmpty(e.Name) ? e.ItemId : e.Name
                    });
                }

                if (e.HasChildren)
                {
                    var childId = string.IsNullOrEmpty(e.ItemId) ? e.Name : e.ItemId;
                    if (visited.Add("branch:" + childId))
                        Recurse(browser, childId, path.Concat(new[] { e.Name }).ToArray(), depth + 1,
                            maxItems, maxDepth, result, visited);
                }
            }
        }
    }
}
