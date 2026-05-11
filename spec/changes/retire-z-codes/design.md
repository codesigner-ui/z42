# Design: Retire Z#### Codes

## Architecture

```
                  Before                                After
            ┌──────────────────┐              ┌──────────────────────┐
            │ docs/error-codes/│              │   (deleted entirely) │
            │     Z.json       │              └──────────────────────┘
            └────────┬─────────┘
                     │ embedded / included by both sides
       ┌─────────────┴─────────────┐
       ▼                           ▼
┌───────────────┐          ┌────────────────────┐
│ Rust runtime  │          │ C# z42c            │
│ diagnostics/  │          │ RustErrorCatalog.cs│
│ mod.rs        │          │ + z42.Core.csproj  │
│               │          │   EmbeddedResource │
└───────┬───────┘          └──────────┬─────────┘
        │                             │
        ▼                             ▼
┌───────────────┐          ┌─────────────────┐
│ z42-vm        │          │ z42c            │
│ --explain     │          │ explain Z####   │
│ --list-errors │          │ (via central    │
│ (CLI)         │          │  DiagnosticCat) │
└───────────────┘          └─────────────────┘


                  After
                                    ┌─────────────────────────────────────┐
                                    │ E#### (compile errors)              │
                                    │   src/compiler/.../DiagnosticCatalog│
                                    │   (C# internal, unchanged)          │
                                    └────────────┬────────────────────────┘
                                                 │
                                                 ▼
                                       z42c explain E0401  (works)
                                       z42c explain Z0905  → friendly hint

                  + VM throw sites split by error semantics:

                  ┌────────────────────────────┐
                  │ user-facing (was Z0908)    │
                  │ → Std.InvalidMarshalException│  (stdlib z42 type)
                  │   with stack trace          │
                  └────────────────────────────┘

                  ┌────────────────────────────┐
                  │ embedder-facing            │
                  │ (was Z0905 / Z0906 / Z0910)│
                  │ → Rust anyhow!(...)        │
                  │   (no Z prefix)            │
                  └────────────────────────────┘

                  ┌────────────────────────────┐
                  │ placeholder (was Z0907)    │
                  │ → constant deleted         │
                  └────────────────────────────┘
```

## Decisions

### Decision 1: Z0908 → `Std.InvalidMarshalException`

**问题**：marshal NUL 错误（原 Z0908）抛 z42 异常时用哪个类型？

**选项**：
- A. `Std.InvalidMarshalException` (新增)
- B. `Std.ArgumentException` (通用)
- C. `Std.InvalidOperationException` (最保守)

**决定**：**A. `Std.InvalidMarshalException`**（User 确认 2026-05-11）。

**理由**：
- 准确：marshal 失败语义清晰，区别于"参数语义错误"或"状态不一致"
- 可扩展：未来 marshal 路径（byte[] 边界、PinnedView 字段越界）触发新 cases 时同类型复用，避免回头再拆
- z42 user 写 catch 时类型 narrowing 更精准（`catch (InvalidMarshalException e)` vs 笼统 `ArgumentException`）

### Decision 2: `docs/error-codes/` 整个目录删除

**问题**：保留目录作为"future error code conventions"占位 vs 整个删除？

**决定**：**整个目录删除**（User 确认）。

**理由**：
- 异常已经是类型化、自描述的；不预期再有"shared catalog"类型的需求
- 留空目录或仅 README.md 是"组织 debt"——新人会困惑"是不是该重新启用 catalog"
- 如果将来真有需要（不太可能），重新创建目录是 1 分钟工作

### Decision 3: `Z0910` 常量完全删除（不重命名为 tag）

**问题**：`src/runtime/src/native/error.rs` 中的 `pub const Z0910: u32 = 910;` 改名 vs 删除？

**决定**：**完全删除**（User 确认）。

**理由**：
- 该常量唯一用处是 anyhow 消息构造时格式化为 `"Z0910: ..."` 前缀
- 移除前缀后常量本身无价值
- 若 loader.rs 等地方仍需要"分类标签"，直接用字符串字面量或 enum variant，不引入新模块

### Decision 4: z-catalog-and-cross-language-sync 归档加注

**问题**：原 spec 引入了部分被本次反向的基础设施。归档怎么处理？

**决定**：**归档目录不动，但 docs/review.md 中加注**（User 确认）。

**实施**：
- `spec/archive/2026-05-XX-z-catalog-and-cross-language-sync/` 目录不动（历史决策记录有价值）
- 在该 spec 的 `tasks.md` 或 `README.md` 顶部加一行 banner：
  ```
  ⚠️ 2026-05-11 NOTE: 本 spec 引入的 Z#### catalog 共享基础设施已由
  `retire-z-codes` (spec/archive/2026-05-11-retire-z-codes/) 整体反向；
  本归档保留作为历史决策追溯。
  ```
- `docs/review.md` 修订记录追加同步条目

## Implementation Notes

### VM exception construction：现有 throw API 复用

`src/runtime/src/exception/` 已有 z42 异常实例构造路径（commit `3d4c790` exception-stack-trace 落地）。本 spec 新需求：从 Rust marshal 失败位置构造 `Std.InvalidMarshalException` 实例并 throw。

**伪代码**：
```rust
// src/runtime/src/native/marshal.rs (new path for Z0908 replacement)
(Value::Str(s), SigType::CStr | SigType::Ptr) => {
    let cs = CString::new(s.as_str()).map_err(|_| {
        // Construct stdlib exception instance + throw via VM trap mechanism
        ctx.throw_stdlib_exception(
            "Std.InvalidMarshalException",
            format!("cannot pass z42 string {:?} as `*const c_char`: contains interior NUL",
                    truncate_for_msg(s))
        )
    })?;
    // ...
}
```

需要确认 `ctx.throw_stdlib_exception(type_fq, message)` API 是否存在；若不存在，需要扩展（小规模，参考 unify-frame-chain 后的 exception 模块）。

**Fallback 路径**：若 stdlib lookup 失败（stdlib 没加载完成），降级为 anyhow! 错误带类型提示：
```
anyhow!("cannot construct Std.InvalidMarshalException (stdlib not loaded?); raw: cannot pass z42 string ...")
```

### 添加 `Std.InvalidMarshalException` 类型

`src/libraries/z42.core/src/Exceptions/InvalidMarshalException.z42`（或 stdlib 当前组织方式相应位置）：

```z42
namespace Std;

/// Thrown when a z42 value cannot be marshalled to a native ABI type
/// due to a contract violation.
///
/// Common causes:
/// - z42 string containing interior NUL passed as `*const c_char`
///   (C consumers cannot disambiguate from the terminator)
/// - PinnedView field accessed beyond {ptr, len}
/// - Unsupported source type for `pinned p = expr { ... }`
///   (currently only string / Array<u8>)
///
/// To avoid: filter inputs before crossing the native boundary, or use
/// a different marshalling path that preserves the binary content
/// (e.g. fixed-length byte arrays for binary data).
public class InvalidMarshalException : Exception {
    public InvalidMarshalException(string message) : base(message) { }
}
```

### z42-vm CLI 改动

`src/runtime/src/main.rs` 的 clap arg struct：

```rust
// 删除以下字段
// - explain: Option<String>
// - list_errors: bool

// 删除以下分支
// if let Some(code) = &cli.explain { diagnostics::explain(...); return Ok(()); }
// if cli.list_errors { print(diagnostics::format_list_all()); return Ok(()); }

// 'file' argument 的 #[arg(required_unless_present_any = [...])] 简化为 required
```

### `dotnet build` 后的 module initializer 链

`RustErrorCatalogModuleInitializer` 类删除后，`Z42.Core` 加载时不再自动注册 Z catalog；但 `DiagnosticCatalog.RegisterExternal` 调用方都消失，整套自动注册链自然瓦解。验证：`dotnet test` 全绿。

### 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| Z0908 测试断言 `msg.contains("Z0908")` | 单测失败 | tasks 显式更新 `marshal_tests.rs` 改为断言新类型 |
| 现有 z42 user code 写了 `catch` raw Z message string | user code 行为变化 | z42 user 没有方式 catch Z 字符串（异常已经是类型 + Message），不影响；只影响 message 文本匹配的（罕见） |
| stdlib 加载顺序导致 `Std.InvalidMarshalException` 不可用 | marshal NUL 时 fallback 路径 | 实施 fallback：构造失败 → 退回 anyhow! 带类型提示 |
| `ctx.throw_stdlib_exception` API 不存在 | 需要扩展 | 看 exception 模块现状，可能需补一个 thin helper（小工作量）|
| `registry_audit_tests.rs` drift-guard 删除后未来加新错误码无 lint | low —— 不再有 catalog 文件，drift 不再可能 | 不缓解（drift 已不存在）|

## Testing Strategy

- **单元测试**：
  - `marshal_tests.rs` 改为断言 `Std.InvalidMarshalException` throw（z42 集成测试）+ 异常 Message 字段内容
  - 删除 `registry_audit_tests.rs` / `src/runtime/src/diagnostics/tests.rs`
- **集成测试**：
  - 新增 `src/tests/parse/marshal_nul_throws/` 或同类目录：z42 程序触发 marshal NUL，断言 catch 到 `Std.InvalidMarshalException`
  - 验证 stack trace 存在
- **回归覆盖**：现有 1186 C# Tests + 既有 VM golden 测试必须 100% 全绿
- **CLI smoke test**：
  - `z42c explain E0401` 仍正常输出
  - `z42c explain Z0905` 输出 friendly hint，退出非 0
  - `z42-vm --explain` 触发 clap 错误

## Estimated Effort

约 0.5 天（含调试 + GREEN）：
- 删除 catalog 基础设施（机械）：0.1 天
- VM throw site 改造（5 处）：0.15 天
- stdlib 类型添加 + 类型 throw helper：0.1 天
- 测试更新：0.1 天
- 文档同步 + 归档加注：0.05 天
