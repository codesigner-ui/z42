# 调试工作流

> **状态**：📋 部分可用。完整 DAP 集成在 SemVer **0.8.7** 启用。

## 当前可用

### 编译器（C#）

VS / VS Code / Rider 设 breakpoint 后：

```bash
dotnet run --project src/compiler/z42.Driver -- <args>
```

直接附加 debugger 即可。

### VM（Rust）

```bash
# lldb（macOS / Linux）
lldb -- ./artifacts/build/runtime/debug/z42vm <file.zbc>

# gdb
gdb --args ./artifacts/build/runtime/debug/z42vm <file.zbc>

# rust-analyzer + VS Code launch.json：照常 cargo run / cargo test
```

### z42 用户代码

z42c 默认产出 debug-symbol 信息：

- **`.zbc`（debug build）**：DBUG section 内嵌 `LineTable` + `LocalVarTable`
- **`.zbc`（release build）+ `.zsym` sidecar**：split-debug-symbols；运行时按 BLAKE3-128 build_id 自动配对加载

VM trace 已自动显示 `<file>:<line>:<col>` + 局部变量名（DBUG section 加载到时）。详见 [`docs/design/runtime/vm-architecture.md`](../design/runtime/vm-architecture.md) "Sidecar 调试符号加载" 段。

```bash
# 产出 sidecar
( cd src/libraries && dotnet run --project ../compiler/z42.Driver -- build --workspace --release )   # release 默认 strip + 产出 .zsym
```

## 0.8.7 之后（DAP debugger）

`z42-dap` 适配层启用后支持：

- VS Code / JetBrains 经 DAP 协议调试 z42 源码
- Step / breakpoint / locals / watch
- JIT / AOT 模式下的 step（详见 Q14 待裁决）

详见 [`docs/roadmap.md`](../roadmap.md) §0.8.x charter。

## 当前 dump 工具

```bash
# zbc 反汇编为 zasm 文本
dotnet run --project src/compiler/z42.Driver -- disasm <file.zbc>

# AST dump（C# 端）
dotnet run --project src/compiler/z42.Driver -- <file.z42> --dump-ast

# Build ID
dotnet run --project src/compiler/z42.Driver -- build-id <file.zbc | file.zpkg | file.zsym>
```
