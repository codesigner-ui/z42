# Proposal: type-based 方法重载决议

## Why

z42c 当前**只支持 arity-only 重载**：方法注册键 `Name$参数个数`，
[`_findMethod`/`_overloadKey`](../../../src/compiler/z42c.semantics/src/TypeChecker.z42) 只按
名字+实参个数查表，**完全不看实参类型**。后果：两个**同 arity** 的重载（如 `F(int)` 与
`F(string)`，或 `F(params string[])` 与 `F(params object[])`）注册时键冲突 → 后者
`Put` 覆盖前者 → 静默丢失。

这阻断了 `add-params-varargs`：其 spec 的重载决议场景（非 params 优先、`string[]` 优于
`object[]`、混类型 fallthrough）全部需要按实参类型在同 arity 候选间择优。本变更给 z42c 补上
**按实参类型的重载决议**，作为 params 全决议的前置。

**关键事实（已核实，决定 scope）**：
- VM 纯按函数名字符串派发（[exec_call.rs](../../../src/runtime/src/interp/exec_call.rs) `func_index.get(fname)`）
  → **runtime 零改动、零新 IR opcode**。
- TSIG 每个方法记录**已携带每参数 TypeName**（[ZpkgWriter._writeMethod](../../../src/compiler/z42c.project/src/ZpkgWriter.z42)）
  → 跨包调用方可从 imported TSIG 重算 type-mangled 名，**无需 zbc/zpkg 格式 bump**。
- 因此本变更是**纯编译器前端**：唯一新机制是重载决议算法 + 同 arity 冲突时的 type-mangling
  （additive 名字，现有无同 arity 重载的代码 emit 不变 → byte-identical 自举不动点保住）。

## What Changes

- **重载决议算法**：调用点对候选集（同名所有重载）按 C# 子集规则择优——
  ① 适用性（每个实参可赋值到对应形参类型，含 boxing/加宽）；② 最具体胜出（更精确类型 / 更少转换）；
  ③ 恰一个最优 → 解析；零 → no-match 错误；并列 → 歧义错误。
- **精确签名序列化 + 决议驱动（B2，User 决策）**：完整参数类型签名**本就序列化在 TSIG**（精确+可序列化）；
  重载决议驱动于该序列化签名（候选集来自序列化签名重建）。方法注册键三档：唯一→`Name`、多 arity→`Name$arity`
  （**均不变**）、**同 arity 多重载→`Name$arity$<typesig>`**（仅这些方法改名）。
- **协议方法固定名约束（硬）**：VM 按字面名派发 `ToString`/`get_`/`set_`/`op_`/`Invoke`/`get_Item` →
  绝不 mangle（它们不会同 arity 重载，天然走前两档）。
- **跨包**：ExportedTypeExtractor/ImportedSymbolLoader 对同 arity 重载用签名键；DependencyIndex type-aware 查表。
  **无 zbc/zpkg 格式 version bump、无字节布局变**（TSIG 参数 TypeName 已在记录中）。
- **字节影响极小**：仅 z42.test/Assert 5 组 long/double 重载新增第二个键（additive，且**修复**当前静默丢失）；
  其余所有方法名不变 → 无 fixture 重生、无 bootstrap 自愈窗口、默认 GREEN 门不破。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42c.semantics/src/SymbolCollector.z42` | MODIFY | 方法用统一签名键注册 + 建 overload-group 索引（name→签名键列表） |
| `src/compiler/z42c.semantics/src/SymbolTable.z42` | MODIFY | overload-group 索引存储 + 候选枚举 API |
| `src/compiler/z42c.semantics/src/Z42Type.z42` | MODIFY | `Z42ClassType` 加 overload-group 索引 + 签名键派生入口 |
| `src/compiler/z42c.semantics/src/Symbol.z42` | MODIFY | `MethodSymbol.RegKey`（注册键=Methods 映射键=IR 名=BoundCall 目标名） |
| `src/compiler/z42c.semantics/src/TypeChecker.z42` | MODIFY | `_resolveOverload`（候选→适用→最具体→歧义）替换 `_overloadKey` 单键逻辑；24 处调用点改走决议 |
| `src/compiler/z42c.semantics/src/OverloadResolver.z42` | NEW | 决议算法 + type-mangling helper（拆出，控 TypeChecker 行数） |
| `src/compiler/z42c.semantics/src/ExportedTypeExtractor.z42` | MODIFY | 按 mangled 名导出方法键 |
| `src/compiler/z42c.semantics/src/ImportedSymbolLoader.z42` | MODIFY | 从 TSIG 参数 TypeName 重建同 arity 候选 + mangled 键 |
| `src/compiler/z42c.semantics/src/IrGen.z42` | MODIFY | IR 函数名用 mangled 键（与注册键一致） |
| `src/compiler/z42c.ir/src/DependencyIndex.z42` | MODIFY | 跨包静态/实例调用 type-aware 查表 |
| `docs/design/language/language-overview.md` | MODIFY | 重载决议规则（从 arity-only 升级为 type-based） |
| `docs/design/compiler/compiler-architecture.md` | MODIFY | 实现原理：决议算法 + mangling + 内存索引策略 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 锁登记/释放 |
| `examples/method_overloads.z42` | NEW | 示例：同 arity 不同类型重载 |
| `src/compiler/z42c.semantics/tests/overload_resolve/` | NEW | 单元：适用性/最具体/歧义/no-match |
| `src/tests/run/overload_by_type/` | NEW | golden：同 arity 类型重载端到端 |
| `src/tests/cross-zpkg/overload_cross_pkg/` | NEW | 跨包：被调方同 arity 重载经 TSIG 正确解析 |

**只读引用**：

- `src/runtime/src/interp/exec_call.rs` — 确认 VM 纯按名派发（无改动）
- `src/compiler/z42c.semantics/src/Z42Type.z42` IsAssignableTo — 适用性判定基元
- `.claude/rules/bootstrap-seed.md` — byte-identical 自举不动点纪律

## Out of Scope

- **VM / runtime 任何改动**（确认纯按名派发）。
- **zbc/zpkg 格式 bump**（参数类型已在 TSIG）。
- **params 变长参数本身**——本变更只交付 type-based 决议；params 决议在 `add-params-varargs` 续做。
- **隐式数值转换的完整 C# 决议表**（int→long→double 等的精确 better-conversion 排序）——
  v1 用最小可用规则（精确 > 加宽/装箱；多个加宽并列 → 歧义报错让用户显式 cast），完整表入 Deferred。

## Open Questions

- [ ] type-mangling 后缀的确定性命名（类型短名拼接 vs FQ 名 vs hash）——需保证跨包调用方可复算（design D2）
- [ ] 现有 stdlib+compiler 是否已有同名同 arity 方法（当前会静默冲突）？→ 实施前全量扫描，确认 byte-identity 假设（tasks 1.0）
