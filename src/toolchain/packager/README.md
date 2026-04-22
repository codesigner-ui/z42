# toolchain/packager — z42 应用打包

## 职责

将 z42 程序打包为可独立分发的应用产物：用户 `.zpkg` + 依赖 `.zpkg` + VM 运行时二进制 + 启动脚本 / 元数据。

与 `scripts/package.sh` 的区别：后者打包 z42 工具链自身（编译器 + VM）；本模块打包 **用户程序**。

不做：编译本身（调用 `compiler/` CLI 完成）。

## 计划模块

尚未实现。预期包含：

| 模块 | 职责 |
|------|------|
| `manifest/` | 读取 `.z42.toml`，解析打包配置（入口、依赖、目标平台） |
| `bundler/` | 收集 `.zpkg`、复制 VM 二进制、生成启动入口 |
| `targets/` | 分平台产物（macOS `.app` / Linux AppImage / Windows `.exe`） |

## 依赖关系

- 依赖 `compiler/z42.Project`（解析 manifest）、`compiler/z42.IR`（zbc 格式）
- 产出消费方：最终用户 / 分发渠道
