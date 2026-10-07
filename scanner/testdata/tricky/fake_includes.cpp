// ============================================================================
// 词法器验证夹具：本文件包含大量"看起来像 include 但不应被识别"的构造。
// 期望扫描结果：nodes=2, links=1 (fake_includes.cpp -> real.h),
//               unresolvedIncludes=1 (仅 <vector>)
// 若解析退化成"正则匹配"，下面的假 include 会产生 6 条假边。
// ============================================================================

// #include "in_line_comment.h"          <- 行注释里的，应忽略

/* #include "in_block_comment.h"          <- 块注释里的，应忽略 */

// 反斜杠续行：下一行在词法上仍属于本注释，应整体忽略 \
#include "in_comment_continuation.h"

const char* s1 = "#include \"in_string.h\"";
const char* s2 = "#include <in_string2.h>";
const char* s3 = R"(#include "in_raw_string.h")";

char hash_ch = '#';                    // 字符字面量中的 '#'，不应进入指令状态

#include "real.h"                      // 唯一一条真实依赖
#include <vector>                      // 系统头：计入 unresolved

int main() { return 0; }
