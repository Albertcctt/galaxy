// ============================================================================
// galaxy-scan — 数据解析端入口
//
// 职责：扫描代码库目录，抽取文件级依赖图（v1：C/C++ #include），按版本化
// schema 输出 galaxy.json，供 Unity 前端渲染为"星系拓扑"。
//
// 用法：galaxy-scan <rootDir> [选项]    详见 --help
// ============================================================================

#include <chrono>
#include <fstream>
#include <iostream>
#include <string>
#include <system_error>
#include <vector>

#include "fs_walk.h"
#include "graph.h"
#include "json_writer.h"
#include "util.h"

#ifdef _WIN32
#include <windows.h>
#include <shellapi.h>   // CommandLineToArgvW
#endif

namespace galaxy {
namespace {

constexpr const char* kScannerId = "galaxy-scan/0.3.0";
constexpr uint64_t kSchemaVersion = 1;  // 契约版本：Unity 端遇到更高版本应拒绝加载

struct CliOptions {
    fs::path root;
    fs::path out = "galaxy.json";
    std::vector<fs::path> include_dirs;   // -I
    std::vector<std::string> excludes;    // -x
    bool pretty = true;
};

void print_usage() {
    std::cout <<
        "galaxy-scan — 代码库星系拓扑扫描器 (schema v1)\n"
        "\n"
        "用法:\n"
        "  galaxy-scan <rootDir> [选项]\n"
        "\n"
        "选项:\n"
        "  -o, --out <file>         输出 JSON 路径 (默认: galaxy.json)\n"
        "  -I, --include-dir <dir>  附加头文件搜索目录（相当于编译器 -I），可重复\n"
        "  -x, --exclude <name>     额外排除的目录名（按名匹配任意层级），可重复\n"
        "      --compact            压缩输出 (默认 2 空格缩进)\n"
        "  -h, --help               显示本帮助\n";
}

enum class ParseResult { Ok, HelpShown, Error };

ParseResult parse_args(const std::vector<std::string>& args, CliOptions& opt) {
    opt.root = from_utf8(args[1]);
    for (size_t i = 2; i < args.size(); ++i) {
        const std::string& a = args[i];
        const auto need_value = [&](const char* what) -> const std::string* {
            if (i + 1 >= args.size()) {
                std::cerr << "错误: " << what << " 缺少参数值\n";
                return nullptr;
            }
            return &args[++i];
        };

        if (a == "-o" || a == "--out") {
            const std::string* v = need_value(a.c_str());
            if (v == nullptr) return ParseResult::Error;
            opt.out = from_utf8(*v);
        } else if (a == "-I" || a == "--include-dir") {
            const std::string* v = need_value(a.c_str());
            if (v == nullptr) return ParseResult::Error;
            opt.include_dirs.push_back(from_utf8(*v));
        } else if (a == "-x" || a == "--exclude") {
            const std::string* v = need_value(a.c_str());
            if (v == nullptr) return ParseResult::Error;
            opt.excludes.push_back(*v);
        } else if (a == "--compact") {
            opt.pretty = false;
        } else if (a == "-h" || a == "--help") {
            print_usage();
            return ParseResult::HelpShown;
        } else {
            std::cerr << "错误: 未知参数 " << a << "\n\n";
            print_usage();
            return ParseResult::Error;
        }
    }
    return ParseResult::Ok;
}

// 按 schema v1 序列化。字段顺序固定 + 上游排序稳定 => 输出逐字节可复现。
void write_json(std::ostream& os, const Graph& g, const fs::path& abs_root, bool pretty) {
    JsonWriter w(os, pretty);
    w.begin_object();
    w.key("schemaVersion"); w.value(kSchemaVersion);
    w.key("scanner");       w.value(kScannerId);
    w.key("root");          w.value(path_to_slashes(abs_root));
    w.key("stats");
    w.begin_object();
    w.key("files");              w.value(static_cast<uint64_t>(g.nodes.size()));
    w.key("links");              w.value(static_cast<uint64_t>(g.links.size()));
    w.key("unresolvedIncludes"); w.value(g.unresolved_includes);
    w.end_object();

    w.key("nodes");
    w.begin_array();
    for (size_t i = 0; i < g.nodes.size(); ++i) {
        const Node& n = g.nodes[i];
        w.begin_object();
        w.key("id");    w.value(static_cast<uint64_t>(i));  // id == 数组下标，Unity 侧直接当索引
        w.key("path");  w.value(n.path);
        w.key("name");  w.value(n.name);
        w.key("dir");   w.value(n.dir);
        w.key("lang");  w.value(n.lang);
        w.key("kind");  w.value(n.kind);   // "text" / "binary"（v0.2 起；旧数据为占位 "file"）
        w.key("bytes"); w.value(n.bytes);
        w.key("lines"); w.value(static_cast<uint64_t>(n.lines));
        w.end_object();
    }
    w.end_array();

    w.key("links");
    w.begin_array();
    for (const Link& l : g.links) {
        w.begin_object();
        w.key("source"); w.value(static_cast<uint64_t>(l.source));
        w.key("target"); w.value(static_cast<uint64_t>(l.target));
        w.key("kind");   w.value("include");
        w.key("weight"); w.value(static_cast<uint64_t>(l.weight));
        w.end_object();
    }
    w.end_array();
    w.end_object();
    os << '\n';
}

int galaxy_main(const std::vector<std::string>& args) {
    if (args.size() < 2) {
        print_usage();
        return 2;
    }
    CliOptions opt;
    switch (parse_args(args, opt)) {
    case ParseResult::HelpShown: return 0;
    case ParseResult::Error:     return 2;
    case ParseResult::Ok:        break;
    }

    std::error_code ec;
    opt.root = fs::absolute(opt.root, ec);
    if (ec || !fs::is_directory(opt.root, ec)) {
        std::cerr << "错误: 目录不存在: " << to_utf8(opt.root) << "\n";
        return 1;
    }

    const auto t0 = std::chrono::steady_clock::now();

    WalkOptions wo;
    wo.extra_excludes = opt.excludes;
    const std::vector<SourceFile> files = walk_sources(opt.root, wo);

    BuildOptions bo;
    bo.include_dirs = opt.include_dirs;
    const Graph g = build_graph(opt.root, files, bo);

    std::ofstream out(opt.out, std::ios::binary | std::ios::trunc);
    if (!out) {
        std::cerr << "错误: 无法写入输出文件: " << to_utf8(opt.out) << "\n";
        return 1;
    }
    write_json(out, g, opt.root, opt.pretty);
    out.close();
    if (!out) {
        std::cerr << "错误: 写入过程中失败（磁盘满？）\n";
        return 1;
    }

    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - t0).count();

    // 摘要走 stdout（给人看）；galaxy.json 内容与时间/耗时无关，保持确定性。
    std::cout << "[galaxy-scan] root       : " << to_utf8(opt.root) << "\n"
              << "[galaxy-scan] files      : " << g.nodes.size() << "\n"
              << "[galaxy-scan] links      : " << g.links.size() << "\n"
              << "[galaxy-scan] unresolved : " << g.unresolved_includes
              << "  (系统头/外部库，未生成节点)\n"
              << "[galaxy-scan] output     : " << to_utf8(fs::absolute(opt.out))
              << " (" << fs::file_size(opt.out, ec) << " bytes)\n"
              << "[galaxy-scan] elapsed    : " << ms << " ms\n";
    return 0;
}

} // namespace
} // namespace galaxy

#ifdef _WIN32

// Windows 入口陷阱：main(int, char**) 的 argv 由 CRT 按 ANSI 代码页
// （中文系统 = GBK）转码，中文路径一进 main 就已经是乱码。正确做法是
// 直接从系统拿 UTF-16 命令行（GetCommandLineW + CommandLineToArgvW），
// 再显式转成 UTF-8 —— 之后整个程序内部统一以 UTF-8 字符串处理。
static std::string wide_to_utf8(const wchar_t* w) {
    const int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return {};
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    s.resize(static_cast<size_t>(n - 1));  // 去掉结尾 '\0'
    return s;
}

int main() {
    SetConsoleOutputCP(CP_UTF8);  // 控制台按 UTF-8 解释输出（中文路径摘要）
    int argc = 0;
    LPWSTR* wargv = CommandLineToArgvW(GetCommandLineW(), &argc);
    std::vector<std::string> args;
    if (wargv != nullptr) {
        args.reserve(static_cast<size_t>(argc));
        for (int i = 0; i < argc; ++i) args.push_back(wide_to_utf8(wargv[i]));
        LocalFree(wargv);
    }
    return galaxy::galaxy_main(args);
}

#else  // POSIX：argv 本身就是字节串（约定 UTF-8），直接使用

int main(int argc, char** argv) {
    return galaxy::galaxy_main(std::vector<std::string>(argv, argv + argc));
}

#endif
