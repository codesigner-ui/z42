# Proposal: Retire Z#### Runtime Error Codes

## Why

VM 运行期错误目前用 `Z####` 编号 + 共享 catalog（`docs/error-codes/Z.json`）+ Rust/C# 双侧加载 + `z42-vm --explain` / `z42c explain` 双工具支持。这套基础设施在 commits `fd5deb2 unify-frame-chain` + `3d4c790 exception-stack-trace` + `820e583 jit-stack-trace-parity` 落地后**变得冗余**：

- **异常已自带 type + message + stack trace 三段自描述**：用户拿到 `Std.InvalidCastException: Index out of range at foo.z42:5:10` 已经能完整定位 → 类型即 mnemonic，无需 Z#### 翻译
- **Z catalog 共享同步**：跨 Rust+C# 双侧维护一份 JSON + 两份加载器 + 一份 drift-guard test，是为了"双工具 explain 输出一致"——但若工具本身可以删，整套基础设施都可以删
- **当前 Z 实际使用极少**：仅 5 条（Z0905/Z0906/Z0907/Z0908/Z0910），且全部是 **native FFI 边界错误**（不是脚本运行期异常），更适合作为**类型化 stdlib 异常** 或 **embedder-facing Rust 错误**两种形态之一

**不做会怎样**：每加一条 VM 错误都要同步 4 处（Rust throw site + Rust catalog + C# catalog + drift-guard test + 可能的 docs）；新 stdlib 异常类型添加也要走"是否分配 Z 编号"的决策，凭空增加心智负担。

## What Changes

### 退役整套 Z 编号 + catalog 基础设施

- ❌ `docs/error-codes/Z.json` — 整个文件删除
- ❌ `src/compiler/z42.Core/Diagnostics/RustErrorCatalog.cs` — 整个文件删除
- ❌ `src/compiler/z42.Core/z42.Core.csproj` 中 `<EmbeddedResource Include="...Z.json" />` 段删除
- ❌ `src/runtime/src/diagnostics/` 模块整个删除（mod.rs + tests.rs）
- ❌ `z42-vm --explain` / `--list-errors` CLI flag 删除
- ❌ Rust drift-guard test（`registry_audit_tests.rs`）删除

### VM throw 站点改造（按错误性质二分）

| Z code | 现状 | 改后归属 | Rationale |
|---|---|---|---|
| **Z0905** | `anyhow!("Z0905: ...")` in `native/registry.rs` | 保留 Rust `anyhow!` 错误，去掉 `Z0905:` 前缀 | 仅 embedder 看见（在 z42 script 运行前的 native binding setup 阶段触发），不是 z42 user-facing 异常；Rust 错误消息直接走 FFI 返回即可 |
| **Z0906** | (常量定义，audit) | 同 Z0905 | 同上 — ABI 版本不匹配是 embedder 编译期 bug |
| **Z0907** | `CallNativeVtable not yet implemented`（placeholder，从未 throw）| 删除整个常量 + catalog 条目 | 占位代码退役 |
| **Z0908** | `anyhow!("Z0908: ...")` in `native/marshal.rs`（z42 string 含 NUL）| **z42 stdlib 异常类型** `Std.InvalidMarshalException`（新增）| 当 z42 user code 通过 `pinned` block 把含 NUL 字符串传 C 时触发；user-catchable |
| **Z0910** | `error::Z0910` 常量 (loader.rs) | 同 Z0905 | dlopen 失败是 embedder layer concern |

### z42c explain 缩窄到 E####

- ✅ `z42c explain E0401` 保留（C# `DiagnosticCatalog` 内部 catalog，独立路径）
- ❌ `z42c explain Z####` 退役（catalog 删了，命令自动失效）
- `z42c explain Z0905` → "Unknown error code Z0905（hint: Z#### codes were retired in 2026-05-11; use the exception's type name)"

### 不变的部分

- ✅ E#### compile-time error codes — 完全不动
- ✅ z42 stdlib 异常类型机制（`Std.Exception` / `Std.IndexOutOfRangeException` 等）— 不动；只增加 `Std.InvalidMarshalException`
- ✅ Stack trace 基础设施 — 不动（这是简化的前提）
- ✅ `Std.Exception.Message` / `.StackTrace` 字段约定 — 不动

## Scope（允许改动的文件）

| 文件路径 | 变更 | 说明 |
|---|---|---|
| `docs/error-codes/Z.json` | DELETE | 整个文件 |
| `docs/error-codes/README.md` | MODIFY | 标记目录已废弃，可选删除整个 docs/error-codes/ 目录 |
| `src/runtime/src/diagnostics/mod.rs` | DELETE | 整个 catalog 加载逻辑 |
| `src/runtime/src/diagnostics/tests.rs` | DELETE | catalog 测试 |
| `src/runtime/src/lib.rs` | MODIFY | 移除 `pub mod diagnostics;` |
| `src/runtime/src/native/registry.rs` | MODIFY | `Z0905:` 前缀从 anyhow 消息中删去 |
| `src/runtime/src/native/marshal.rs` | MODIFY | `Z0908:` 路径改为构造 `Std.InvalidMarshalException` z42 异常（需 vm exception construction helper）|
| `src/runtime/src/native/loader.rs` | MODIFY | `Z0910:` 同 Z0905（去前缀）|
| `src/runtime/src/native/error.rs` | MODIFY / DELETE | `Z0910` 常量 + 相关代码删除或重命名为非 Z 前缀 |
| `src/runtime/src/native/marshal_tests.rs` | MODIFY | 删除 `assert!(msg.contains("Z0908"))` 改为断言新 stdlib 异常类型 |
| `src/runtime/src/native/registry_audit_tests.rs` | DELETE 或 MODIFY | drift-guard test 删除（catalog 没了）|
| `src/runtime/src/main.rs` | MODIFY | 删除 `--explain` / `--list-errors` flag |
| `src/compiler/z42.Core/Diagnostics/RustErrorCatalog.cs` | DELETE | 整个文件 |
| `src/compiler/z42.Core/z42.Core.csproj` | MODIFY | 删除 `<EmbeddedResource Include="...Z.json" />` |
| `src/libraries/z42.core/` 或 `src/libraries/z42.exceptions/`（新建）| MODIFY/NEW | 添加 `Std.InvalidMarshalException` 类型 |
| `src/runtime/src/native/` (VM exception construction) | MODIFY | 当 marshal 失败时构造 stdlib 异常对象 throw（需找到/扩展现有 VM exception throw API）|
| `src/compiler/z42.Tests/` | MODIFY | 如有引用 RustErrorCatalog / Z 编号的测试，更新或删除 |
| `docs/review.md` | MODIFY | 状态同步：retire-z-codes 落地，z-catalog-cross-language-sync 反向 |

**只读引用**：
- 所有 `*.rs` 文件搜索 `Z[0-9]{4}` 确认无遗漏（特别是 doc comments）
- `src/runtime/src/exception/` 现有 z42 异常构造路径（指导 Z0908 → InvalidMarshalException 改造）

## Out of Scope

- E#### compile-time catalog 重构（保留现状）
- 其他 stdlib 异常类型增补（仅加 `Std.InvalidMarshalException`）
- VM exception throw helper API 重新设计（用现有的）
- 添加新的 z42c explain 输出格式 / JSON output 等增强
- 与 review.md 中其他未完成项目联动（独立 spec）

## Open Questions

- [ ] **Z0908 → 哪个 stdlib 异常类型？** 候选: `Std.InvalidMarshalException`（最准确）/ `Std.ArgumentException`（更通用）/ `Std.InvalidOperationException`（最保守）。Decision 留 design.md
- [ ] **`docs/error-codes/` 整个目录是否删除？** 若未来无类似 catalog 需求 → 删；若保留作为"future error code conventions"占位 → 留 README.md。Decision 留 design.md
- [ ] **`src/runtime/src/native/error.rs` 中的 Z0910 常量**是否完全删除，还是改为 `NATIVE_LOAD_FAILURE_TAG: &str = "native-load"` 内部 tag？Decision 留 design.md
- [ ] **是否同时清理 z-catalog-and-cross-language-sync spec 的归档**？该 spec 引入了部分被本次反向的基础设施。倾向**不动归档**（历史决策依然有记录价值），但在 docs/review.md 中加注"于 2026-05-11 反向"
