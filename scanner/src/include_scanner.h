#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace galaxy {

// 一条 #include 记录（未解析；target 保留源码原样）
struct IncludeRef {
    std::string target;  // 例如 "core/parser.h" 或 "stdio.h"
    bool quoted = false; // true: "..."   false: <...>
    uint32_t line = 0;   // 1-based 行号（诊断用；Unity 端未来可做行级定位）
};

// 从 C/C++ 源码文本中抽取所有 #include。
//
// 实现是真正的词法状态机（正确处理注释/字符串/字符字面量/原始字符串/行拼接），
// 不是正则 —— 因此 `const char* s = "#include <fake.h>";` 或注释里的
// #include 不会产生假边。宁可漏报、绝不误报。
//
// v1 已知局限（对短小的解析器而言可接受，tree-sitter 升级后消除）：
//   - 不处理 #if 0 块（禁用代码块里的 include 仍会被记录）
//   - 不展开宏（#include MACRO 记不到目标）
std::vector<IncludeRef> scan_includes(const std::string& source);

} // namespace galaxy
