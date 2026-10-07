#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "fs_walk.h"

namespace galaxy {

// 图模型。节点 id 即 nodes 数组下标（稠密整数）——
// 序列化后 Unity 端可直接当数组索引使用，无需哈希。
struct Node {
    std::string path;    // 相对根的路径，"src/core/parser.cpp"（永远 '/' 分隔）
    std::string name;    // 文件名 "parser.cpp"
    std::string dir;     // 所在目录 "src/core"（根目录下的文件为 "."）
    std::string lang;    // C 家族 "cpp"/"c"；其余 = 扩展名（无点小写）
    std::string kind;    // "text" / "binary"（头部字节嗅探判定；查看器可读性分类）
    uint64_t bytes = 0;
    uint32_t lines = 0;  // 文本行数；二进制与超阈值文件为 0
};

struct Link {
    uint32_t source = 0;  // 起点：nodes 下标（依赖发出方）
    uint32_t target = 0;  // 终点：nodes 下标（被依赖方）
    uint32_t weight = 1;  // 重复 include 次数（可视化时可映射为能量流粗细）
};

struct Graph {
    std::vector<Node> nodes;
    std::vector<Link> links;
    uint64_t unresolved_includes = 0;  // 未能解析到扫描树内的 include（系统头/外部库）
};

struct BuildOptions {
    std::vector<fs::path> include_dirs;  // -I：附加搜索目录（仅当位于扫描根内部才生效）
    // 超过该大小的文本文件保留节点但不读内容（行数 0、不抽依赖）——巨型生成
    // 文件（字库 .c 这类）跳过全量 读取，避免把扫描时间花在数不清的行上
    uint64_t max_line_count_bytes = 8ull * 1024 * 1024;
};

// 两遍构建：第 1 遍建路径索引，第 2 遍读取文件、抽取 include、解析成边。
// links 按 (source, target) 排序输出，保证确定性。
Graph build_graph(const fs::path& root,
                  const std::vector<SourceFile>& files,
                  const BuildOptions& opt);

} // namespace galaxy
