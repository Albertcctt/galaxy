#pragma once

// 通用工具：集中在"表示边界"的转换上 —— 文件系统宽字符 <-> UTF-8、路径归一化。
// 全部 header-only：为一个几行的转换函数单独建编译单元得不偿失。
#include <algorithm>
#include <cctype>
#include <filesystem>
#include <string>

namespace galaxy {

namespace fs = std::filesystem;

// path -> UTF-8。Windows 下 path 内部是 wchar_t(UTF-16)，直接 .string()
// 会按 ANSI 代码页窄化，中文路径就毁了；u8string() 才是正确出口。
// （C++20 起 u8string() 返回 u8string/char8_t，届时此处需适配；本项目锁定 C++17。）
inline std::string to_utf8(const fs::path& p) {
    return p.u8string();
}

// UTF-8 -> path。Windows 上绝不能写 path(utf8_str)：该构造函数把窄串
// 按 ANSI 代码页解释。u8path 是 C++17 的正确入口（C++20 可用 u8string 构造）。
inline fs::path from_utf8(const std::string& s) {
    return fs::u8path(s);
}

inline std::string to_lower_ascii(std::string s) {
    for (char& c : s) {
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    return s;
}

// 路径 -> 正斜杠 UTF-8 字符串（写 JSON / 显示一律用它，平台无关）。
inline std::string path_to_slashes(const fs::path& p) {
    std::string s = to_utf8(p);
    std::replace(s.begin(), s.end(), '\\', '/');
    return s;
}

// 查表键：正斜杠 + 全小写。Windows 文件系统大小写不敏感，而源码里
// #include 的拼写不受控（Foo.h vs foo.h），统一小写查表最稳。
inline std::string normalize_key(const fs::path& rel) {
    return to_lower_ascii(path_to_slashes(rel.lexically_normal()));
}

} // namespace galaxy
