# z42 开发指南

> 本文档收录 z42 项目的所有构建、编译、测试、打包命令。
> 工作流规则见 [.claude/rules/workflow.md](../.claude/rules/workflow.md)。

---

## Quick Start: just

日常构建 / 测试 / 打包统一入口走 [`just`](https://github.com/casey/just)。

### 安装 just

```bash
brew install just                       # macOS
cargo install just                      # 通用（任何已装 cargo 的环境）
sudo apt install just                   # Ubuntu 22.04+ / Debian
scoop install just                      # Windows (scoop)
```

### 常用命令

```bash
just                # 列出所有 task（= just --list）
just build          # 编译器 + 运行时
just test           # 全部测试（compiler + VM + cross-zpkg）
just test-vm        # 仅 VM golden tests
just test-vm jit    # 用 JIT 模式
just clean          # 清空 artifacts/
just ci             # CI 标准管线（= build + test）
```

> 占位 task：`just bench` / `just test-changed` / `just test-stdlib` / `just platform <x> <y>` 当前会输出 "Pn 待实施" 并 exit 1，等对应 sub-spec 实施后启用。
>
> 现有 `./scripts/*.sh` 仍可独立运行（justfile 内部就是调用它们），向后兼容。

---

## 构建

```bash
# 编译器（C# bootstrap）
dotnet build src/compiler/z42.slnx

# 运行时（Rust VM）
cargo build --manifest-path src/runtime/Cargo.toml
```

---

## 编译器命令

### 单文件模式

```bash
dotnet run --project src/compiler/z42.Driver -- <file.z42> [--emit ir|zbc] [-o <out>]
```

- `--emit zbc`：产出 `.zbc` 字节码（VM 可直接执行）
- `--emit ir`：产出 ZASM 文本（调试查看用）

### 项目模式（`.z42.toml`）

```bash
dotnet run --project src/compiler/z42.Driver -- build [<name>.z42.toml] [--release] [--bin <name>]
dotnet run --project src/compiler/z42.Driver -- check [<name>.z42.toml] [--bin <name>]
dotnet run --project src/compiler/z42.Driver -- run   [<name>.z42.toml] [--release] [--bin <name>] [--mode interp|jit|aot]
dotnet run --project src/compiler/z42.Driver -- clean [<name>.z42.toml]
```

### 工具命令

```bash
dotnet run --project src/compiler/z42.Driver -- disasm <file.zbc> [-o <file.zasm>]
dotnet run --project src/compiler/z42.Driver -- explain <ERROR_CODE>
dotnet run --project src/compiler/z42.Driver -- errors
```

---

## 运行 VM

```bash
cargo run --manifest-path src/runtime/Cargo.toml -- <file.zbc | file.zpkg> [--mode interp|jit|aot]
```

> VM 通过文件扩展名分发：`.zbc` 走 `load_zbc`、`.zpkg` 走 `load_zpkg`（见
> `src/runtime/src/metadata/loader.rs::load_artifact`）。

---

## 测试

```bash
# 编译器 golden tests
dotnet test src/compiler/z42.Tests/z42.Tests.csproj

# VM 测试（interp + jit 双模式）
./scripts/test-vm.sh

# 跨 zpkg 端到端测试（target lib + ext lib + main app 三方协作）
./scripts/test-cross-zpkg.sh
```

> 修改编译器后，先 `--emit zbc` 重新生成 `.zbc`，再跑 `./scripts/test-vm.sh`。
> `test-cross-zpkg.sh` 用例放在 `src/runtime/tests/cross-zpkg/`，
> 详见该目录的 README。

---

## 打包与发行

```bash
# 打包：compiler + VM binary + stdlib libs → artifacts/z42/
./scripts/package.sh            # debug build（z42c single-file + z42vm）
./scripts/package.sh release    # release build

# 编译标准库：src/libraries/ workspace 模式 → artifacts/libraries/<lib>/dist/<lib>.zpkg
#                                            → artifacts/libraries/<lib>/cache/<file>.zbc (debug 模式时)
./scripts/build-stdlib.sh              # 使用 dotnet run 编译
./scripts/build-stdlib.sh --use-dist   # 使用打包后的 z42c 编译

# 直接走 z42c workspace 模式编译 stdlib（等价于 build-stdlib.sh 中间步骤）：
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release )

# 增量编译（C5，默认开启）：第二次构建跳过未变文件
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release )           # cached: N/N
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release --no-incremental )   # 强制全量

# 打包分发：把 artifacts/libraries/<lib>/dist/<lib>.zpkg 拷到 artifacts/z42/libs/<lib>.zpkg
./scripts/package.sh         # 必要时再跑 build-stdlib.sh，然后再 package.sh 拷贝

# 发行包端到端测试（使用 artifacts/z42/bin/ 的 z42c + z42vm）
./scripts/test-dist.sh              # 编译+运行 golden tests（interp + jit）
./scripts/test-dist.sh interp       # 仅 interp 模式
```

> `artifacts/z42/` 与 `artifacts/libraries/` 均已在 `.gitignore` 中，不纳入版本控制。
> 修改标准库源文件后需重新运行 `./scripts/build-stdlib.sh` 更新 zpkg 产物。

> **artifacts 目录分工**：
> - `artifacts/libraries/<lib>/dist/` — workspace 模式 stdlib **构建产物**（每 lib 一个子目录）
> - `artifacts/libraries/<lib>/cache/` — 中间产物 .zbc（debug/indexed 模式时生成）
> - `artifacts/z42/libs/` — 分发版 stdlib 扁平布局（package.sh 拷贝目标）
> - `artifacts/z42/bin/` — 分发版 z42c / z42vm 单文件 binary
>
> `build-stdlib.sh` 仅写 `artifacts/libraries/`；`package.sh` 在打包阶段把
> `<lib>/dist/<lib>.zpkg` 拷到 `artifacts/z42/libs/<lib>.zpkg`。
> 发行包测试全流程：`package.sh` → `build-stdlib.sh --use-dist` → `test-dist.sh`。

---

## 全绿（GREEN）标准

完整定义见 [.claude/rules/workflow.md 阶段 8](../.claude/rules/workflow.md)。简言之：

```bash
dotnet build src/compiler/z42.slnx                          # 无编译错误
cargo build --manifest-path src/runtime/Cargo.toml          # 无编译错误
dotnet test src/compiler/z42.Tests/z42.Tests.csproj         # 100% 通过
./scripts/test-vm.sh                                        # 100% 通过
```

任何测试失败（含 pre-existing）都不得 commit / push。
