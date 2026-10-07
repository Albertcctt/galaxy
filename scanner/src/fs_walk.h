#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "util.h"

namespace galaxy {

// 扫描到的一个候选文件（v0.2 起：任意常规文件，不再限 C/C++）
struct SourceFile {
    fs::path abs;           // 绝对路径（本机文件系统操作）
    fs::path rel;           // 相对扫描根的路径（序列化用，跨机器可移植）
    std::string lang_id;    // C 家族："c"/"cpp"；其余：扩展名（无点小写；无扩展名 = ""）
    bool c_family = false;  // 是否按 C/C++ 预处理规则抽取 include
    uint64_t bytes = 0;
};

struct WalkOptions {
    std::vector<std::string> extra_excludes;   // --exclude：按目录名排除（仅存的手动过滤）
};

// 递归遍历 root，返回全部常规文件（真·全部：含隐藏目录/构建产物，由调用方决定
// 是否用 --exclude 手动过滤；仅硬性跳过符号链接目录（防环）与无权限目录）。
// 保证：结果按归一化相对路径字典序排序（输出确定性）。
std::vector<SourceFile> walk_sources(const fs::path& root, const WalkOptions& opt);

} // namespace galaxy
