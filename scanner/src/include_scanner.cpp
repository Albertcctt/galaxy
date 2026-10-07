#include "include_scanner.h"

#include <cctype>
#include <string_view>

namespace galaxy {
namespace {

// ---------------------------------------------------------------------------
// 预处理阶段 2（行拼接）：删除所有 "\\\n"（反斜杠+换行）序列。
// 按标准，行拼接发生在词法分析之前 —— 所以 `// 注释 \` 会吃掉下一行、
// `#inc\` + `lude "x.h"` 能拼成合法指令。先拼接再词法化，这两类跨行
// 写法才能正确处理。拼接同时维护 字节->原始行号 映射，诊断行号不偏移。
// ---------------------------------------------------------------------------
struct SplicedSource {
    std::string text;
    std::vector<uint32_t> line;  // line[i] = text[i] 在原始文件中的行号（1-based）

    explicit SplicedSource(const std::string& raw) {
        text.reserve(raw.size());
        line.reserve(raw.size());
        uint32_t cur = 1;
        for (size_t i = 0; i < raw.size(); ++i) {
            if (raw[i] == '\\' && i + 1 < raw.size() && raw[i + 1] == '\n') {
                i += 1;  // 吃掉反斜杠+换行，逻辑行继续
                continue;
            }
            if (raw[i] == '\\' && i + 2 < raw.size() && raw[i + 1] == '\r' && raw[i + 2] == '\n') {
                i += 2;  // CRLF 变体
                continue;
            }
            const char c = raw[i];
            text.push_back(c);
            line.push_back(cur);
            if (c == '\n') ++cur;
        }
    }
};

enum class St { Normal, LineComment, BlockComment, StringLit, CharLit, RawString };

class Lexer {
public:
    explicit Lexer(const SplicedSource& s) : m_src(s.text), m_line(s.line) {}

    std::vector<IncludeRef> run() {
        std::vector<IncludeRef> out;
        while (m_i < m_src.size()) {
            const char c = m_src[m_i];
            switch (m_state) {
            case St::LineComment:
                ++m_i;
                if (c == '\n') { m_state = St::Normal; m_bol = true; }
                continue;

            case St::BlockComment:
                if (c == '*' && peek(1) == '/') { m_state = St::Normal; m_i += 2; continue; }
                if (c == '\n') m_bol = true;  // 注释等价于一个空格，不破坏"行首"性质
                ++m_i;
                continue;

            case St::StringLit:
            case St::CharLit:
                if (c == '\\') { m_i += 2; continue; }  // 转义：连跳两字节
                if (c == '\n') { m_state = St::Normal; m_bol = true; ++m_i; continue; }  // 容错：未闭合字面量
                if ((m_state == St::StringLit && c == '"') ||
                    (m_state == St::CharLit && c == '\'')) { m_state = St::Normal; ++m_i; continue; }
                ++m_i;
                continue;

            case St::RawString:
                if (c == ')' && raw_string_ends()) { m_state = St::Normal; m_i += m_delim.size() + 2; continue; }
                if (c == '\n') m_bol = true;
                ++m_i;
                continue;

            case St::Normal:
                break;
            }

            // ---- Normal 状态 ----
            if (c == '\n') { m_bol = true; ++m_i; continue; }
            if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v') { ++m_i; continue; }
            if (c == '/' && peek(1) == '/') { m_state = St::LineComment; m_i += 2; continue; }
            if (c == '/' && peek(1) == '*') { m_state = St::BlockComment; m_i += 2; continue; }
            if (c == '"')  { m_state = St::StringLit; m_bol = false; ++m_i; continue; }
            if (c == '\'') { m_state = St::CharLit;   m_bol = false; ++m_i; continue; }
            if (c == '#' && m_bol && try_include(out)) continue;

            // 原始字符串 R"delim(...)delim"（对 u8R/uR/UR/LR 前缀做近似边界判断）
            if (c == 'R' && peek(1) == '"' && raw_prefix_ok()) {
                size_t p = m_i + 2;
                const size_t d0 = p;
                while (p < m_src.size() && p - d0 < 16 &&
                       m_src[p] != '(' && m_src[p] != '"' && m_src[p] != '\n') {
                    ++p;
                }
                if (p < m_src.size() && m_src[p] == '(') {
                    m_delim.assign(m_src, d0, p - d0);
                    m_state = St::RawString;
                    m_bol = false;
                    m_i = p + 1;
                    continue;
                }
            }
            m_bol = false;
            ++m_i;
        }
        return out;
    }

private:
    char peek(size_t off) const {
        const size_t p = m_i + off;
        return p < m_src.size() ? m_src[p] : '\0';
    }

    bool raw_prefix_ok() const {
        if (m_i == 0) return true;
        const char pc = m_src[m_i - 1];
        if (pc == '8') return true;  // u8R" 前缀的一部分
        return !(std::isalnum(static_cast<unsigned char>(pc)) || pc == '_');
    }

    bool raw_string_ends() const {
        const size_t p = m_i + 1;  // 跳过 ')'
        if (m_src.compare(p, m_delim.size(), m_delim) != 0) return false;
        const size_t q = p + m_delim.size();
        return q < m_src.size() && m_src[q] == '"';
    }

    // 当前位置为行首 '#'。若是 include 指令则记录目标并吞掉整行（返回 true，
    // 主循环继续）；不是 include 的其他指令同样吞行（指令体内不可能有 #include）。
    bool try_include(std::vector<IncludeRef>& out) {
        size_t j = m_i + 1;
        while (j < m_src.size() && (m_src[j] == ' ' || m_src[j] == '\t')) ++j;
        const size_t id0 = j;
        while (j < m_src.size() &&
               (std::isalpha(static_cast<unsigned char>(m_src[j])) || m_src[j] == '_')) {
            ++j;
        }
        const std::string_view directive(&m_src[id0], j - id0);
        if (directive != "include") { skip_line(); return true; }

        while (j < m_src.size() && (m_src[j] == ' ' || m_src[j] == '\t')) ++j;
        if (j >= m_src.size()) { skip_line(); return true; }
        const char open = m_src[j];
        if (open != '"' && open != '<') { skip_line(); return true; }  // 如 #include SOME_MACRO
        const char close = (open == '"') ? '"' : '>';
        const size_t t0 = ++j;
        while (j < m_src.size() && m_src[j] != close && m_src[j] != '\n') ++j;
        if (j < m_src.size() && m_src[j] == close) {
            IncludeRef ref;
            ref.target.assign(m_src, t0, j - t0);
            ref.quoted = (open == '"');
            ref.line = m_line[t0];
            out.push_back(std::move(ref));
        }
        skip_line();
        return true;
    }

    void skip_line() {
        while (m_i < m_src.size() && m_src[m_i] != '\n') ++m_i;  // 保留 '\n' 交给主循环
    }

    const std::string& m_src;
    const std::vector<uint32_t>& m_line;
    size_t m_i = 0;
    bool m_bol = true;             // 是否处于"行首（仅空白）"位置
    St m_state = St::Normal;
    std::string m_delim;           // 原始字符串的分隔符（可为空）
};

} // namespace

std::vector<IncludeRef> scan_includes(const std::string& source) {
    const SplicedSource spliced(source);
    return Lexer(spliced).run();
}

} // namespace galaxy
