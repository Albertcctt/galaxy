#pragma once

// 扩展名 -> 语言 的静态映射表。
// v1 只产出 C/C++ 节点；c_family=true 的文件会被 include 词法器扫描。
// 扩展点：未来加 "py"/"ts"/"cs" 等，只需补充此表 + 对应的依赖抽取器，
// 图构建与序列化层完全不用动。
#include <string>
#include <string_view>
#include <unordered_map>

namespace galaxy {

struct LangInfo {
    const char* id;      // 写进 galaxy.json 的 null-terminated 语言标识
    bool c_family;       // 是否按 C/C++ 预处理规则抽取依赖
};

inline const LangInfo* detect_lang(std::string_view ext_no_dot) {
    static const std::unordered_map<std::string_view, LangInfo> kMap = {
        {"c",   {"c",   true}},
        {"h",   {"cpp", true}},   // 头文件按 C++ 处理（.h 无法从扩展名区分 C/C++，是常见取舍）
        {"cpp", {"cpp", true}},
        {"cc",  {"cpp", true}},
        {"cxx", {"cpp", true}},
        {"hpp", {"cpp", true}},
        {"hh",  {"cpp", true}},
        {"hxx", {"cpp", true}},
        {"inl", {"cpp", true}},
        {"ipp", {"cpp", true}},
        {"tpp", {"cpp", true}},
    };
    const auto it = kMap.find(ext_no_dot);
    return it == kMap.end() ? nullptr : &it->second;
}

} // namespace galaxy
