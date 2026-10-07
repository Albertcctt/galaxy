cmake_minimum_required(VERSION 3.16)
project(galaxy_scan VERSION 0.1.0 LANGUAGES CXX)

# 全项目锁定 C++17：u8path/u8string 的语义随之确定（C++20 起返回类型有变）。
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_executable(galaxy-scan
    src/main.cpp
    src/fs_walk.cpp
    src/graph.cpp
    src/include_scanner.cpp
)

if(WIN32)
    # main.cpp 使用 CommandLineToArgvW 获取 UTF-16 命令行（见该文件注释）
    target_link_libraries(galaxy-scan PRIVATE shell32)
endif()

if(MSVC)
    # /utf-8：源码为 UTF-8（含中文注释），不指定时 MSVC 按本地代码页解析会告警/乱码
    target_compile_options(galaxy-scan PRIVATE /W4 /utf-8)
else()
    target_compile_options(galaxy-scan PRIVATE -Wall -Wextra)
endif()

set_target_properties(galaxy-scan PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
