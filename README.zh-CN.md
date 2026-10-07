# galaxy

**把任意代码库变成一张三维星系图。**

每个文件是一颗天体，`#include` 依赖是连线，项目的第一层文件夹是一个个
互不干涉的"宇宙"球体。

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![Unity](https://img.shields.io/badge/Unity-6000.6%20URP-black)
![C++](https://img.shields.io/badge/C%2B%2B-17-blue)

*[English →](README.md)*

## 截图

![多元宇宙 —— lvgl 全库 1887 个文件分布在 7 个互不干涉的宇宙球中](docs/images/01-multiverse.png)

![选中态 —— 依赖链点亮，双击打开代码查看器](docs/images/02-selection-viewer.png)

![桌面应用 —— 二进制文件是红球，散文件宇宙中心是一颗动态黑洞](docs/images/03-desktop-app.png)

## 特性

- **四种文件天体形态**
  - 头文件（`.h/.hpp/.hh/.hxx/.inl/.ipp/.tpp`）→ 透明立方体
  - 源文件（`.c/.cpp/.cc/.cxx`）→ 透明正四面体
  - 其他文本文件 → 按目录色相的自发光球
  - 二进制文件 → **红色球**（查看器无法预览的标识）
- **宇宙球** —— 每个第一层文件夹一枚菲涅尔边缘光环球体；半径 = 1.9 ×
  ∛文件数，打包排布保证互不干涉
- **层级引力核（递归星团场）** —— 每个子目录一个固定引力核、越深引力越强，
  球内凝聚出不对称的星团与空隙，而非均匀填满
- **黑洞** —— 无目录归属的散文件宇宙中心是一颗小型动态黑洞（黑色核心 +
  双层虚线吸积环反向旋转、呼吸脉动），尺寸 ∝ ∛文件数
- **零穿插硬保证** —— 任意实体包络球不重叠（位置投影 + 局部间隙半径收缩）
- **拾取与高亮** —— 单击天体点亮其依赖链（金线照穿前景）+ 全场压暗 +
  文件详情面板
- **内置代码查看器** —— 双击文本文件打开 VS Code 风格面板（深色主题、行号、
  C/C++ 语法高亮、滚轮翻页、可拖拽滚动条）；二进制文件给出明确提示
- **OPEN PROJECT** —— 桌面应用内随时选择任意文件夹重扫重建；扫描器随包分发，
  上次打开的项目会被记住

## 工作原理

```
scanner/（C++17）                     galaxy_project/（Unity 6.6，URP）
  遍历文件 ──┐                        JSON → 力导向布局按"宇宙优先"分区 →
  文本/二进制 ├─► galaxy.json ──────► 实体网格 + 连线网格 + HUD +
  嗅探       │   （确定性输出，        拾取 / 代码查看器 / 桌面应用
  解析 #include┘    schema v1）
```

扫描器输出**逐字节确定**：节点按归一化路径排序、边按 `(source, target)`
排序——同一份代码永远得到相同的 `galaxy.json`，可提交、可 diff、可回归测试。

## 快速开始

### 前置

- **CMake** ≥ 3.16 + C++17 编译器（构建扫描器）
- **Unity 6000.6**（URP，用于渲染端 / 桌面应用）

### 1. 构建扫描器

```bash
cmake -S scanner -B scanner/build -G Ninja
cmake --build scanner/build
```

### 2. 扫描项目

```bash
scanner/build/bin/galaxy-scan <项目根目录> -o scanner/galaxy.json
```

```
用法: galaxy-scan <rootDir> [选项]
  -o, --out <file>         输出 JSON 路径（默认 galaxy.json）
  -I, --include-dir <dir>  附加头文件搜索目录（等价编译器 -I，可重复）
  -x, --exclude <name>     按目录名排除（任意层级，可重复）
      --compact            压缩输出
  -h, --help               帮助
```

默认扫描**全部文件**（含隐藏目录与构建产物）；仅硬性跳过符号链接目录
（防遍历成环）与无权限目录。大仓库建议 `-x .git -x build` 手动裁剪。

### 3. 运行

**桌面应用（Windows）** —— 通过编辑器内构建脚本（`Assets/Galaxy/Editor/
GalaxyBuildPlayer.cs`）产出到 `dist/Galaxy/`，然后：

```
dist\Galaxy\Galaxy.exe                      # 内置 lvgl 演示 / 上次的项目
dist\Galaxy\Galaxy.exe --scan "D:\某项目"   # 启动即扫描
```

应用内点 **OPEN PROJECT** 可用系统对话框选择文件夹。扫描过程实时走秒、
可随时取消；超过 1 万节点的项目会拒绝重建并提示选择子目录。

**Unity 编辑器** —— 用 Unity 6000.6 打开 `galaxy_project/`，加载
`Assets/Scenes/SampleScene.unity` 后 Play。数据路径解析顺序：显式 jsonPath →
上次打开的项目（PlayerPrefs）→ 仓库默认 `scanner/galaxy.json`。

## 操作说明

| 输入 | 效果 |
|---|---|
| 左键单击天体 | 高亮依赖链 + 文件详情 |
| 左键**双击** | 打开代码查看器（文本文件） |
| 拖拽 / 滚轮 / 右键拖拽 | 旋转 / 缩放 / 平移 |
| 查看器内滚轮 | 翻页代码 |
| 拖拽查看器滚动条 | 直接跳到目标位置 |
| 指针在面板上 | 相机输入整体让位 |

## 仓库结构

```
scanner/         C++17 扫描器：遍历 + 文本/二进制嗅探 + #include 抽取
galaxy_project/  Unity 6.6 URP 前端
  Assets/Galaxy/Scripts/   运行时（布局 / 实体 / 拾取 / 查看器 / 加载器）
  Assets/Galaxy/Editor/    工具链（场景装配 / 自动演示 / 构建脚本）
  Assets/Galaxy/Shaders/   自定义 shader（叠加线 / 菲涅尔气泡）
  Assets/StreamingAssets/  随包扫描器与演示数据（构建用）
docs/images/     README 用截图
```

## 数据契约（`galaxy.json`，schema v1）

```
nodes[]  { id, path, name, dir, lang, kind, bytes, lines }
         kind: "text" | "binary"（头部 8KB 字节嗅探；旧数据为占位 "file"）
links[]  { source, target, kind: "include", weight }
stats    { files, links, unresolvedIncludes }
```

## 已知限制

- 依赖抽取目前只支持 C/C++（`#include`）；其他语言的文件是节点但无连线
- 布局复杂度 O(n²)，渲染上限约 1 万节点（超出时应用拒绝重建并提示）
- 桌面构建目前仅 Windows；exe 使用 Unity 默认图标
- 界面文案为英文；代码注释与日志为中文

## 开发

自动化重跑（标记文件注入、双截图验证）、Unity 陷阱清单与调试铁律见
**`CLAUDE.md`**。

## 许可

[GPL-3.0](LICENSE)
