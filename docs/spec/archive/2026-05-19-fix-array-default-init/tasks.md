# Tasks: fix `new T[N]` default initialization

> 状态：🟢 已完成 | 创建：2026-05-18 | 完成：2026-05-19

## 进度概览
- [x] 阶段 1：IR / 编译器侧（C#）— ArrayNewInstr 携带 ElemType
- [x] 阶段 2：zbc / zpkg wire format — 写 + 读 + version bump
- [x] 阶段 3：运行时侧（Rust）— Instruction.ArrayNew、interp、JIT
- [x] 阶段 4：单元 + golden 测试
- [x] 阶段 5：Workaround 清理（Dictionary、Sha256）
- [x] 阶段 6：版本 bump 同步（fixtures regen + changelog）
- [x] 阶段 7：全绿验证 + 归档

## 阶段 1：IR / 编译器侧（C#）

- [x] 1.1 `src/compiler/z42.IR/IrModule.cs:274` — `ArrayNewInstr` 加 `IrType ElemType` 字段
- [x] 1.2 `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs:281` — `VisitArrayCreate` 把 `ac.ElemType` 经 `ToIrType` 写入 `ArrayNewInstr`
- [x] 1.3 `src/compiler/z42.IR/IrVerifier.cs` — `ArrayNewInstr` 解构若涉及新字段则补 `_`（无需改动 — verifier 模式匹配不解构 ElemType）
- [x] 1.4 `src/compiler/z42.IR/BinaryFormat/ZasmWriter.cs:195` — `ArrayNewInstr` 文本格式追加 `<{i.ElemType}>`

## 阶段 2：zbc / zpkg wire format

- [x] 2.1 `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs:185` — `ArrayNewInstr` emission 末尾追加 `w.Write(TypeTagFromIrType(i.ElemType))`
- [x] 2.2 `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs:205` — 解码追加 `r.ReadByte()` 并经 `IrTypeFromTag` 转回
- [x] 2.3 `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` — `VersionMinor`: 5 → 6
- [x] 2.4 `src/compiler/z42.Project/ZpkgWriter.cs` — `VersionMinor`: 6 → 7

## 阶段 3：运行时侧（Rust）

- [x] 3.1 `src/runtime/src/metadata/bytecode.rs:519` — `Instruction::ArrayNew` 加 `elem_tag: u8`（含 `#[serde(default)]` 占位）
- [x] 3.2 `src/runtime/src/metadata/zbc_reader.rs:32-44` — `ZBC_VERSION_MINOR`: 5 → 6；`ZPKG_VERSION_MINOR`: 6 → 7
- [x] 3.3 `src/runtime/src/metadata/zbc_reader.rs:860` — `OP_ARRAY_NEW` 解码追加 `c.read_u8()?` 拿 elem_tag
- [x] 3.4 `src/runtime/src/metadata/types.rs` — 新增 `TAG_*` 常量 + `default_value_for_tag(tag: u8) -> Value`
- [x] 3.5 `src/runtime/src/interp/exec_array.rs:12` — `array_new` 签名加 `elem_tag: u8`，用 `default_value_for_tag` 替换 `Value::Null`
- [x] 3.6 `src/runtime/src/interp/exec_instr.rs:152` — match arm 透传 `elem_tag`
- [x] 3.7 `src/runtime/src/interp/mod.rs` — 仅 doc 注释提及 ArrayNew，无需改签名
- [x] 3.8 `src/runtime/src/jit/helpers/array.rs:9` — `jit_array_new` 签名加 `elem_tag: u8`，用 `default_value_for_tag`（删除重复 match 分支）
- [x] 3.9 `src/runtime/src/jit/translate.rs:660` — match arm 解构 `elem_tag` 并经 `iconst(types::I8, ...)` 传入 host call
- [x] 3.10 `src/runtime/src/jit/helpers/registry.rs:249` — `array_new` Cranelift signature 加 `i8t`

## 阶段 4：单元 + golden 测试

- [x] 4.1 `src/runtime/src/metadata/types_tests.rs`（NEW）— 7 个测试覆盖 bool / signed int / unsigned int / float / char / ref / 与 string-key 同步性
- [x] 4.2 `src/runtime/src/metadata/mod.rs` — 加 `#[cfg(test)] mod types_tests;`
- [x] 4.3 golden tests — 通过 stdlib regen 覆盖（每个 stdlib + golden test source 重生时都会触发 ArrayNew 新 wire；FormatGoldenTests / FormatInvariantTests 通过）
- [x] 4.4 演示 — Dictionary / Sha256 workaround 删除即为最强演示（`new byte[N]` 在真实 stdlib 代码里直接走 default 路径）

## 阶段 5：Workaround 清理

- [x] 5.1 `src/libraries/z42.core/src/Collections/Dictionary.z42` — 删构造函数的 init loop + 配套注释
- [x] 5.2 `src/libraries/z42.core/src/Collections/Dictionary.z42` — 删 `Grow()` 的 init loop
- [x] 5.3 `src/libraries/z42.crypto/src/Sha256.z42` — 删 `_zeroBytes` helper
- [x] 5.4 `src/libraries/z42.crypto/src/Sha256.z42` — 顶部 gotcha 注释删除
- [x] 5.5 `src/libraries/z42.crypto/src/Sha256.z42` — 所有 `_zeroBytes(n)` 调用点替换为 `new byte[n]`

## 阶段 6：版本 bump 同步

- [x] 6.1 `docs/design/runtime/zbc.md` — Minor changelog 加 `1.6 | 2026-05-19 | fix-array-default-init`
- [x] 6.2 `docs/design/runtime/zpkg.md` — Minor changelog 加 `0.7 | 2026-05-19 | fix-array-default-init`
- [x] 6.3 `docs/design/runtime/ir.md` — `ArrayNew` 指令文档（无独立段落；语义描述足够覆盖）
- [x] 6.4 `./src/tests/zbc-format/generate-fixtures.sh` 跑一遍 — 6 fixture 全部 regen
- [x] 6.5 `./src/tests/zpkg-format/generate-fixtures.sh` 跑一遍 — 4 fixture 全部 regen
- [x] 6.6 `./scripts/regen-golden-tests.sh release` 跑一遍 — 171 golden 全部 regen + stdlib 17 包重生

## 阶段 7：全绿验证 + 归档

- [x] 7.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] 7.2 `cargo build --manifest-path src/runtime/Cargo.toml --release` 无错
- [x] 7.3 `./scripts/test-all.sh` 全绿（6/6 stages）
- [x] 7.4 spec scenarios 逐条对照验证（所有 default-value scenarios + version bump scenarios + workaround cleanup scenarios）
- [x] 7.5 `docs/roadmap.md` — 未涉及 pipeline 阶段进度变化
- [ ] 7.6 commit + push（pending）
- [ ] 7.7 归档（pending）

## 实施期发现 + 处理

### Scope 扩展：`force_load_all_declared` 单 pass bug

**根因**：[`src/runtime/src/metadata/lazy_loader.rs`](../../../../src/runtime/src/metadata/lazy_loader.rs) 的 `force_load_all_declared` 只 snapshot `remaining_declared()` 一次再迭代。`load_zpkg_file` 加载每个 zpkg 时会把它声明的 transitive deps 加入 `declared_zpkgs` —— 但这一 pass 看不到新加入的条目，导致 transitive 依赖的 zpkg 的 `__static_init__` 永远不跑。

**触发症状**：当用户脚本只 import `Std.Crypto`（z42.crypto），z42.crypto 内部用 `Std.Encoding.Hex`，但 z42.encoding 是 transitive dep → `Std.Encoding.Hex.__static_init__` 不跑 → `Hex.ALPHA_LOWER` 保持 `Null` → 首次 `.CharAt(...)` 抛 `__str_char_at: arg 0 expected string, got Null`。

**为何 fix-array-default-init 暴露**：原 `Sha256.test_empty_string` 也踩这条路径（同样 error message），原本就 fail；commit 1160cf12 知情发布、列为 known issue。本 spec 清掉 Sha256 内的 `_zeroBytes` workaround 时，必须把 `test_empty_string` 也带回 GREEN（不能借口 "pre-existing" 跳过 GREEN 标准）。

**修复**：`force_load_all_declared` 改为 `loop { snapshot; if empty break; for each load; }` —— 一次 load 把新 dep 加入后下一轮 snapshot 自然包含。代价为 0（loop 一旦没有新增就退出）。

**Scope 影响**：在 proposal Scope 之外（新增 `src/runtime/src/metadata/lazy_loader.rs` MODIFY）。决策：扩展 Scope 而非降级处理 —— 该 bug 在 GREEN 路径上必须修，否则 5.x workaround 清理无法验证。spec workflow §"Scope 扩展" 允许在根因修复时扩展。

## 备注

- **变更分类**：vm + ir（带 wire format 变更）→ 走完整 spec 流程
- **实际 wire impact**：每个 `new T[N]` 站点 zbc 多 1 字节
- **回归风险点（已验证）**：JIT signature 变更 + Cranelift type 注册 → `./scripts/test-vm.sh` 全绿
- **后续工作**（不在本 spec）：HMAC-SHA256 / `Std.Crypto.Random` 等 — 见 [`docs/design/stdlib/crypto.md`](../../../design/stdlib/crypto.md) Deferred
