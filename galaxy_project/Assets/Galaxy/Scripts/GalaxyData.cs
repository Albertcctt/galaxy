// ============================================================================
// galaxy.json 的运行时模型 + 加载器（schema v1）。
//
// 与 C++ 端的契约（见 scanner/src/main.cpp 的 write_json）：
//   - 用 Unity 内置 JsonUtility 解析：它按"字段名精确匹配"反射赋值
//     （大小写敏感、缺字段静默留默认值），所以 DTO 字段名必须与 JSON key
//     逐字一致 —— 这里的 camelCase 是数据契约，不是命名风格问题。
//   - 加载时显式校验 schemaVersion：契约版本不匹配要立即失败，
//     而不是带着半个图继续跑（这正是版本字段存在的意义）。
// ============================================================================
using System;
using System.IO;
using UnityEngine;

namespace Galaxy
{
    [Serializable]
    public class GalaxyNode
    {
        public int id;
        public string path;
        public string name;
        public string dir;
        public string lang;
        public string kind;
        public long bytes;
        public int lines;
    }

    [Serializable]
    public class GalaxyLink
    {
        public int source;   // 依赖发出方（nodes 下标）
        public int target;   // 被依赖方（nodes 下标）
        public string kind;
        public int weight;   // 重复 include 次数
    }

    [Serializable]
    public class GalaxyStats
    {
        public int files;
        public int links;
        public int unresolvedIncludes;
    }

    [Serializable]
    public class GalaxyGraph
    {
        public int schemaVersion;
        public string scanner;
        public string root;
        public GalaxyStats stats;
        public GalaxyNode[] nodes;
        public GalaxyLink[] links;
    }

    public static class GalaxyLoader
    {
        public const int SupportedSchemaVersion = 1;

        // 从磁盘读取并校验。失败返回 null（错误已 LogError，调用方只需判空）。
        public static GalaxyGraph Load(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("[Galaxy] json 路径为空");
                return null;
            }
            if (!File.Exists(path))
            {
                Debug.LogError($"[Galaxy] 文件不存在: {path}");
                return null;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Galaxy] 读取失败: {path}\n{e.Message}");
                return null;
            }

            GalaxyGraph graph;
            try
            {
                graph = JsonUtility.FromJson<GalaxyGraph>(json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Galaxy] JSON 解析失败（文件损坏？）: {path}\n{e.Message}");
                return null;
            }

            if (graph == null || graph.nodes == null || graph.nodes.Length == 0)
            {
                Debug.LogError("[Galaxy] 解析结果为空或没有节点");
                return null;
            }
            if (graph.links == null) graph.links = Array.Empty<GalaxyLink>();

            if (graph.schemaVersion != SupportedSchemaVersion)
            {
                Debug.LogError($"[Galaxy] schema 版本不匹配: 文件 v{graph.schemaVersion}, " +
                               $"本代码支持 v{SupportedSchemaVersion}。请更新渲染端或重新扫描。");
                return null;
            }

            // 一致性防御：link 端点必须落在 nodes 下标范围内。
            // 损坏的数据在这里被拦下，而不是让越界索引在下游"随机炸"。
            foreach (GalaxyLink link in graph.links)
            {
                if (link.source < 0 || link.source >= graph.nodes.Length ||
                    link.target < 0 || link.target >= graph.nodes.Length)
                {
                    Debug.LogError($"[Galaxy] 数据损坏: link {link.source}->{link.target} 越界");
                    return null;
                }
            }

            Debug.Log($"[Galaxy] 已加载 {graph.nodes.Length} 节点 / {graph.links.Length} 边 " +
                      $"(扫描器 {graph.scanner}, 根 {graph.root})");
            return graph;
        }
    }
}
