#pragma once

// 极简 JSON 写入器：只覆盖本工具输出的子集（对象/数组/字符串/无符号整数）。
// 取舍：用 ~130 行自持代码换零第三方依赖 —— 构建系统永远不需要再更新。
// 若未来 schema 需要浮点或更复杂的动态结构，再考虑引入 nlohmann/json。
#include <cstdint>
#include <cstdio>
#include <ostream>
#include <string>
#include <vector>

namespace galaxy {

class JsonWriter {
public:
    JsonWriter(std::ostream& os, bool pretty) : m_os(os), m_pretty(pretty) {}

    void begin_object() { open_container('{'); }
    void end_object()   { close_container('}'); }
    void begin_array()  { open_container('['); }
    void end_array()    { close_container(']'); }

    void key(const std::string& k) {
        before_item();
        write_quoted(k);
        m_os << (m_pretty ? ": " : ":");
        m_after_key = true;
    }

    void value(const std::string& v) {
        if (!m_after_key) before_item();
        m_after_key = false;
        write_quoted(v);
    }

    void value(uint64_t v) {
        if (!m_after_key) before_item();
        m_after_key = false;
        m_os << v;
    }

private:
    void open_container(char c) {
        if (!m_after_key) before_item();
        m_after_key = false;
        m_os << c;
        m_first.push_back(true);
    }

    void close_container(char c) {
        const bool empty = m_first.back();
        m_first.pop_back();
        if (!empty && m_pretty) { m_os << '\n'; indent(); }
        m_os << c;
    }

    // 写"元素/键"之前的统一点：逗号分隔 + 换行缩进
    void before_item() {
        if (m_first.empty()) return;
        if (!m_first.back()) m_os << ',';
        m_first.back() = false;
        if (m_pretty) { m_os << '\n'; indent(); }
    }

    void indent() {
        for (size_t i = 0; i < m_first.size(); ++i) m_os << "  ";
    }

    void write_quoted(const std::string& s) {
        m_os << '"';
        for (const char raw : s) {
            const unsigned char c = static_cast<unsigned char>(raw);
            switch (c) {
            case '"':  m_os << "\\\""; break;
            case '\\': m_os << "\\\\"; break;
            case '\b': m_os << "\\b"; break;
            case '\f': m_os << "\\f"; break;
            case '\n': m_os << "\\n"; break;
            case '\r': m_os << "\\r"; break;
            case '\t': m_os << "\\t"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                    m_os << buf;
                } else {
                    m_os << raw;  // >=0x20：含 UTF-8 高位字节，按原文输出
                }
            }
        }
        m_os << '"';
    }

    std::ostream& m_os;
    bool m_pretty;
    std::vector<bool> m_first;   // 每层容器：是否还没写过元素（逗号控制）
    bool m_after_key = false;
};

} // namespace galaxy
