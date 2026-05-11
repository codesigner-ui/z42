# workflow/building/

z42 各组件的本地构建命令与产物布局。

## 核心文件

| 文件 | 职责 |
|------|------|
| [`compiler.md`](compiler.md) | C# Bootstrap 编译器（dotnet）+ z42c 单文件 / 项目模式 / 工具命令 |
| [`vm.md`](vm.md) | Rust VM（cargo）+ feature flags + 默认执行模式 |
| [`stdlib.md`](stdlib.md) | 6 个 stdlib 包 workspace 编译 + 增量缓存 + artifacts 同步 |
| [`cross-platform.md`](cross-platform.md) | 跨平台编译矩阵（macOS / Linux / Windows × x86_64/arm64 / WASM）— **placeholder 0.2.5** |

## 通用入口

绝大多数场景用 `just` 即可：

```bash
just build       # 编译器 + VM
just clean       # 清 artifacts/
```

按文件查命令时优先看本目录子文件。

## 安装 `just`

```bash
brew install just                # macOS
cargo install just               # 通用（已装 cargo 即可）
sudo apt install just            # Ubuntu 22.04+ / Debian
scoop install just               # Windows
```

> `./scripts/*.sh` 仍可独立运行（justfile 内部就是调用它们）；保留向后兼容。
