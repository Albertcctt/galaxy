#include "fs_walk.h"

#include <algorithm>
#include <system_error>

#include "language.h"

namespace galaxy {
namespace {

// 目录过滤：默认"真·全部"（含隐藏目录/构建产物——用户选择）；唯一的手动过滤
// 是 --exclude（按目录名匹配，任意层级，语义简单可预测、比 glob 好调试）。
// 硬性跳过项只剩两个安全机制：符号链接目录（防环）、无权限目录（读不了）。
bool is_excluded_dir(const std::string& name, const WalkOptions& opt) {
    for (const auto& d : opt.extra_excludes) {
        if (name == d) return true;
    }
    return false;
}

} // namespace

std::vector<SourceFile> walk_sources(const fs::path& root, const WalkOptions& opt) {
    std::vector<SourceFile> out;
    std::error_code ec;

    // 显式栈迭代遍历（而非递归）：目录树再深也不会爆栈，控制流也更好推理。
    std::vector<fs::path> stack{root};
    while (!stack.empty()) {
        fs::path dir = std::move(stack.back());
        stack.pop_back();

        fs::directory_iterator it(dir, fs::directory_options::skip_permission_denied, ec);
        if (ec) continue;  // 目录不可读：跳过，不中断整体扫描
        for (const auto& entry : it) {
            const std::string name = to_utf8(entry.path().filename());
            if (entry.is_directory(ec)) {
                if (is_excluded_dir(name, opt)) continue;
                if (entry.is_symlink(ec)) continue;  // 符号链接目录：跳过，防止遍历成环
                stack.push_back(entry.path());
            } else if (entry.is_regular_file(ec)) {
                const uint64_t size = entry.file_size(ec);
                if (ec) continue;   // 取 size 失败（竞态删除等）：跳过
                SourceFile f;
                f.abs = entry.path();
                f.rel = entry.path().lexically_relative(root);

                // C 家族仍按扩展名表判定（享受 include 抽取）；其余文件 lang_id
                // 记扩展名本身（无点小写；Makefile/LICENSE 这类无扩展名为 ""）
                const std::string ext = to_lower_ascii(to_utf8(entry.path().extension()));
                const std::string ext_no_dot =
                    (ext.size() >= 2 && ext[0] == '.') ? ext.substr(1) : std::string();
                const LangInfo* lang = ext_no_dot.empty() ? nullptr : detect_lang(ext_no_dot);
                f.c_family = lang != nullptr && lang->c_family;
                f.lang_id = f.c_family ? lang->id : ext_no_dot;
                f.bytes = size;
                out.push_back(std::move(f));
            }
        }
    }

    // 按归一化相对路径排序：让输出顺序与目录遍历顺序解耦。
    // 副作用是 galaxy.json 逐字节可复现 —— 可提交、可 diff、可写回归测试。
    std::sort(out.begin(), out.end(), [](const SourceFile& a, const SourceFile& b) {
        return normalize_key(a.rel) < normalize_key(b.rel);
    });
    return out;
}

} // namespace galaxy
