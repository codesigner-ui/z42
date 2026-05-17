# Tasks: refactor corelib to typed zero-clone extractors

> 状态：🟡 进行中 | 创建：2026-05-17 | 类型：refactor（runtime VM 内部，无外部行为变更）
> Spec 类型：minimal mode

## 背景

Phase 1 of "native function call → direct ABI"（用户在 refactoring analysis 上
要求的"彻底"路径）：在不动 dispatch ABI（`fn(&VmContext, &[Value]) -> Result<Value>`
不变）的前提下，把每个 builtin 内部的 arg extraction 做到：

- **零 clone**：string args 拿 `&str` 而非 `String`（当前 `require_str` 每次 `s.clone()`）
- **零间接**：所有 helper `#[inline]`，编译器可把 match 内联到 caller
- **类型化**：i64 / bool / char / f64 等 Copy 类型直接按 value 传

## 范围

- 现状：128 个 builtin，79 处 `require_str` / `require_usize` / `require_i64` 调用
- 本 spec scope：introduce 新 typed helpers + 迁移 `corelib/string.rs`（13 个 builtin，热路径首选 dogfood）
- 后续 commit 分批迁移：char.rs / math.rs / object.rs / fs.rs / array.rs / system.rs / process.rs / convert.rs / platform.rs / io.rs / gc.rs / bench.rs

## 不在范围

- 改 dispatch ABI（要求改 interp/exec_native + native/dispatch 同步，是 L2）
- 改 BUILTINS registry 类型（保持 `&[(&str, BuiltinFn)]`）
- 改 Value enum 结构
- 引入 `builtin!` proc/declarative macro（等所有手工迁移完后看是否有重复模式值得抽）

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. helper 命名 | `arg_str` / `require_str_ref` / `take_str` | `arg_str` | 短、贴近 caller 视角；新 vs 旧不易混淆 |
| 2. helper 模块位置 | 新增 `corelib/args.rs` / 复用 `corelib/convert.rs` | 复用 convert.rs | 同主题（参数转换），避免拆碎 |
| 3. 旧 `require_str` 处理 | deprecate 标记 / 立即删除 | 立即删除（按本 spec 节奏分批跟进所有 caller） | pre-1.0 不留兼容包袱 |
| 4. lifetime | `arg_str<'a>(args: &'a [Value]) -> Result<&'a str>` | yes | 标准 borrow；caller 短期持有 |
| 5. `#[inline]` 策略 | 全部加 / 只热路径 | 全部加（helper 自身简单） | match-and-return 是 inline 候选 |
| 6. usize 范围检查 | 保留 `arg_usize` 的越界检查 | yes | 不能因 perf 砍验证；同 require_usize |
| 7. 错误信息格式 | 与 require_* 一致 | yes | 现有测试可能依赖 |
| 8. 首批迁移文件 | string.rs / char.rs / math.rs 三选一 | string.rs | 13 个 builtin，全 `require_str`，模式纯净 |

## 阶段 1: typed helpers

- [ ] 1.1 MODIFY `src/runtime/src/corelib/convert.rs`
  - 加 `#[inline] arg_str<'a>(args: &'a [Value], idx, ctx) -> Result<&'a str>`
  - 加 `#[inline] arg_i64(args, idx, ctx) -> Result<i64>`
  - 加 `#[inline] arg_usize(args, idx, ctx) -> Result<usize>`
  - 加 `#[inline] arg_bool(args, idx, ctx) -> Result<bool>`
  - 加 `#[inline] arg_char(args, idx, ctx) -> Result<char>`
  - 加 `#[inline] arg_f64(args, idx, ctx) -> Result<f64>`
  - 旧 `require_str` / `require_usize` / `require_i64` / `to_*` 保留（其他文件还在用，本 spec 只迁 string.rs）

## 阶段 2: migrate string.rs

- [ ] 2.1 MODIFY `src/runtime/src/corelib/string.rs`
  - 13 个 builtin 全部 `require_str(args, n, ctx)?` → `arg_str(args, n, ctx)?`
  - body 内 `let s = ...` 现在是 `&str`，所有 `.clone()` / `&*s` 清掉
  - `require_usize` → `arg_usize`

## 阶段 3: GREEN

- [ ] 3.1 `cargo build --manifest-path src/runtime/Cargo.toml --release` 通过
- [ ] 3.2 `cargo test --manifest-path src/runtime/Cargo.toml` 通过（含 corelib/string_tests）
- [ ] 3.3 `./scripts/test-stdlib.sh` 全绿（stdlib string API 验证）
- [ ] 3.4 `dotnet test src/compiler/z42.Tests` 全绿（golden tests 间接通过 VM 用 string builtin）

## 阶段 4: 归档

- [ ] 4.1 mv → `docs/spec/archive/2026-05-17-refactor-corelib-typed-extractors/`
- [ ] 4.2 commit + push（首批 = string.rs，~13 builtin 改完，确立模式）

## 实施期发现

1. **`to_usize` 现有重复 match arm bug**（convert.rs:55-56）：两个相同 pattern
   `Value::I64(n) if *n >= 0 => Ok(*n as usize)` —— 第二个永远不可达。属本 spec
   scope 外的预存 bug，没顺手改（按 workflow.md "Scope 内 only" 原则）。Backlog
   候选：单独 commit 删第二行。
2. **首批迁移仅 string.rs**（6 个 builtin → ~10 处 `require_str`/`require_usize`
   替换为 `arg_str`/`arg_usize`）；模式确立后 followup commit 批量迁 char/math/
   object/fs/array/system/process/convert/platform/io/gc/bench（剩 12 个文件）。
3. **GREEN 验证遇到的环境噪声**：`test-stdlib.sh` 报 2 个 file failed，但根因
   是 parallel uncommitted PriorityQueue.z42/SortedDictionary.z42 generics
   constraint 错误（z42.collections 编译失败 → blocks 下游 z42.regex）。临时
   `mv` 出去后 61/61 全绿，验证本 refactor 自身无回归。`dotnet test`（1288/1288）
   和 `cargo test`（392/392 lib + 各子 suite）也全绿。
