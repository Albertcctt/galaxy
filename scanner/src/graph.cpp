#include "graph.h"

#include <algorithm>
#include <fstream>
#include <map>
#include <sstream>
#include <system_error>
#include <unordered_map>

#include "include_scanner.h"
#include "util.h"

namespace galaxy {
namespace {

bool read_file(const fs::path& p, std::string& out) {
    std::ifstream in(p, std::ios::binary);
    if (!in) return false;
    std::ostringstream ss;
    ss << in.rdbuf();
    out = std::move(ss).str();
    return true;
}

// 只读文件头（文本/二进制嗅探用）：二进制判定不需要全量读 —— .git 对象、
// 资源文件动辄上百 MB，全量读入纯属浪费。
bool read_head(const fs::path& p, std::string& out, size_t n) {
    std::ifstream in(p, std::ios::binary);
    if (!in) return false;
    out.resize(n);
    in.read(out.data(), static_cast<std::streamsize>(n));
    out.resize(static_cast<size_t>(in.gcount()));
    return true;
}

// 二进制启发式：文件头 8KB 内出现 NUL 字节即视为二进制。任何主流二进制格式
//（ELF/PNG/压缩流/字节码）头部必然含 0x00；代价是 UTF-16 文本会被误判为
// 二进制 —— 罕见格式，接受这个取舍（业界通用做法）。
bool is_binary_head(const std::string& head) {
    return head.find('\0') != std::string::npos;
}

uint32_t count_lines(const std::string& s) {
    if (s.empty()) return 0;
    const auto n = static_cast<uint64_t>(std::count(s.begin(), s.end(), '\n'));
    return static_cast<uint32_t>(s.back() == '\n' ? n : n + 1);
}

} // namespace

Graph build_graph(const fs::path& root, const std::vector<SourceFile>& files,
                  const BuildOptions& opt) {
    Graph g;
    g.nodes.reserve(files.size());

    // ---- 第 1 遍：归一化路径 -> 节点下标 ----
    // 类似一张开放定址哈希索引；后面的 include 解析就是在这张表里做几次查找。
    std::unordered_map<std::string, uint32_t> by_path;
    by_path.reserve(files.size() * 2);
    for (const auto& f : files) {
        Node n;
        n.path = path_to_slashes(f.rel);
        n.name = to_utf8(f.rel.filename());
        n.dir = f.rel.parent_path().empty() ? std::string(".")
                                            : path_to_slashes(f.rel.parent_path());
        n.lang = f.lang_id;
        n.bytes = f.bytes;
        by_path.emplace(normalize_key(f.rel), static_cast<uint32_t>(g.nodes.size()));
        g.nodes.push_back(std::move(n));
    }

    // -I 目录预先折算成"相对扫描根"的形式：只有落在根内部的目录才能映射到节点；
    // 根外目录（如系统 include 路径）直接忽略。
    const fs::path abs_root = fs::absolute(root);
    std::vector<fs::path> inc_rel;
    for (const auto& d : opt.include_dirs) {
        const fs::path rel = fs::absolute(d).lexically_relative(abs_root);
        if (rel.empty() || *rel.begin() == fs::path("..")) continue;
        inc_rel.push_back(rel);
    }

    // ---- 第 2 遍：逐文件嗅探文本/二进制；文本数行（C 家族再抽 include）----
    // 边先经 map 去重并累计权重（同一对文件多次 include 合并为一条带 weight 的边）。
    std::map<std::pair<uint32_t, uint32_t>, uint32_t> edge_weight;
    for (size_t id = 0; id < files.size(); ++id) {
        const SourceFile& f = files[id];
        Node& node = g.nodes[id];

        // 文本/二进制嗅探（所有文件都做，只读头部 8KB）
        std::string head;
        if (!read_head(f.abs, head, 8192)) continue;   // 读不了（竞态删除）：留空节点
        const bool binary = is_binary_head(head);
        node.kind = binary ? "binary" : "text";
        if (binary) continue;   // 二进制：不数行、不抽依赖（查看器也不该打开）

        // 巨型文本：保留节点但跳过全量读（行数 0；C 家族的 include 也放弃 ——
        // 原实现对这类生成文件同样是跳过，只是现在还保留节点以示存在）
        if (f.bytes > opt.max_line_count_bytes) continue;

        std::string src;
        if (!read_file(f.abs, src)) continue;
        node.lines = count_lines(src);

        if (!f.c_family) continue;   // 非 C 文本：本版只做节点，不抽依赖

        for (const IncludeRef& ref : scan_includes(src)) {
            const fs::path target = from_utf8(ref.target);
            uint32_t dst = UINT32_MAX;
            // 解析顺序（简化版编译器行为）：
            //   1) 相对当前文件所在目录    2) 相对扫描根    3) 各 -I 目录
            const auto try_key = [&](const fs::path& rel_to_root) {
                const auto it = by_path.find(normalize_key(rel_to_root));
                if (it != by_path.end()) dst = it->second;
            };
            try_key(files[id].rel.parent_path() / target);
            if (dst == UINT32_MAX) try_key(target);
            for (const fs::path& inc : inc_rel) {
                if (dst != UINT32_MAX) break;
                try_key(inc / target);
            }

            if (dst == UINT32_MAX) { ++g.unresolved_includes; continue; }  // 系统头/外部库
            if (dst == id) continue;                                       // 自环无信息量
            ++edge_weight[{static_cast<uint32_t>(id), dst}];
        }
    }

    // map 迭代天然按 (source, target) 升序 —— links 输出确定性由此保证。
    g.links.reserve(edge_weight.size());
    for (const auto& [key, w] : edge_weight) {
        g.links.push_back(Link{key.first, key.second, w});
    }
    return g;
}

} // namespace galaxy
