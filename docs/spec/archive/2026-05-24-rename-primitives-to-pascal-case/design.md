# Design: rename primitive types to BCL-style PascalCase

## Architecture

两层映射：

```
  用户代码层                  编译器 / TypeChecker             VM 运行时
  ─────────                  ──────────────────              ────────
  int x = 5;       ◄────►   Z42PrimType("int")     ◄────►   Value::I64(5)
   long y;         ◄────►   Z42PrimType("long")    ◄────►   Value::I64(_)
   i8 z;           ◄────►   Z42PrimType("i8")      ◄────►   Value::I64(_)
                                  │                            ▲
                                  │ keyword → stdlib FQN map   │
                                  ▼                            │
                          Std.Int32 / Std.Int64 / Std.SByte ───┘
                          （新名；旧名 Std.int / Std.long / Std.i8）
                          ↓
                          zbc.ConstStrPool 存的 class FQN
                          ↓
                          VM 用 module.func_index[FQN.<method>] 路由
```

**关键不变**：
- 用户代码层（keyword）不变
- Z42PrimType 内部 ID（小写）不变 — 避免上下游震荡
- VM `Value` enum（6 variants 已存在）不变
- zbc wire format 不变 — 仅 ConstStrPool 里的字符串值改

**关键变化**：
- TypeChecker `keyword → stdlib FQN` 映射表从 5 条扩 13 条，且值全部 PascalCase
- stdlib 12 个 primitive struct 名 + 文件名 PascalCase
- VM `well_known_names.rs` 6 个 STD_* 常量值更新（不增不减）
- Native binding 字符串 key + Rust `builtin_*` 函数名 同步 rename

## Decisions

### Decision 1: Z42PrimType 内部 ID 是否同步 PascalCase？

**问题**：`Z42Type.Int = new Z42PrimType("int")` 的 string ID "int" 是否改为 "Int32"？

**选项**：
- A：内部 ID 保持小写（`"int" / "long" / "i8" / ..."`）
- B：内部 ID 同步改为 PascalCase（`"Int32" / "Int64" / "SByte" / ..."`）

**决定**：**A** —— 内部 ID 是 TypeChecker 跨 pass 的稳定 join key（也存在 IrType.Tag 比较中），改它会触发：
- 所有 `Z42Type.Int == Z42Type.Int` 比较失效（按引用相等保持，但 ID 字段比较可能用到）
- TypeRegistry `CanonicalName: "int"` 改成 `"Int32"` → `IrTypeByName` lookup 同步
- zbc TypeTag 字段（如有 string emit）需 minor bump

选 A 的代价是：TypeChecker 需要一个**显式映射函数** `PrimKeywordToStdlibClass(string keyword) → string fqn`，但这函数本就要写（switch table 集中在一处）。

### Decision 2: 映射表的"权威居所"

**问题**：keyword → stdlib FQN 的 13 条映射应该住在哪个文件？避免多处同步漂移。

**选项**：
- A：直接放在 `TypeChecker.Calls.cs` 的 switch（当前位置，最简）
- B：放进 `TypeRegistry.cs` 的 `TypeEntry` 加一个 `StdlibClassName` 字段
- C：放进 `z42.IR/WellKnownNames.cs`（C# 侧 mirror Rust 的 well_known_names）

**决定**：**B** —— TypeRegistry 已经是"primitive 类型元数据 SoT"。增加一个 `StdlibClassName` 字段：

```csharp
new TypeEntry("int",  ["i32"], IrType.I32, Z42Type.Int,  true, true, false, ..., StdlibClassName: "Std.Int32"),
new TypeEntry("long", ["i64"], IrType.I64, Z42Type.Long, true, true, false, ..., StdlibClassName: "Std.Int64"),
// ...
```

`TypeChecker.Calls.cs::BindMemberCallOnUnknownTarget` 改为：
```csharp
string? resolvedClassName = TypeRegistry.GetTypeEntry(tgtName)?.StdlibClassName ?? tgtName;
```

这样：
- 13 条映射定义在一个地方
- `IrTypeByName` / `ResolveType` / `BindMemberCallOnPrimitive` 等也能复用同一个字段
- 未来加新 primitive 只改一行

### Decision 3: Rust well_known_names.rs 是否补 6 个新常量？

**问题**：`Std.Int64 / Std.SByte / Std.Int16 / Std.Byte / Std.UInt16 / Std.UInt32 / Std.UInt64 / Std.Single` 这些 FQN 是否需要在 Rust 端 hardcode 常量？

**调研发现**：grep 显示 Rust 代码中**没有任何地方**硬编码 `"Std.long" / "Std.float" / "Std.i8" / "Std.u8" / "Std.u16" / "Std.u32" / "Std.u64"` 等字符串（除了 well_known_names.rs 自身的常量定义之外）。它们的 class name 全由 C# 编译器 emit 进 zbc ConstStrPool，VM 用 `module.func_index[FQN]` 直接查找，不依赖 Rust 端常量。

**决定**：**不补新常量**。6 现有常量（`STD_INT / STD_LONG / STD_DOUBLE / STD_FLOAT / STD_BOOL / STD_CHAR`）改值即可，常量名同时改为对应的 BCL 形式：

```rust
pub const STD_INT32:  &str = "Std.Int32";     // was STD_INT
pub const STD_INT64:  &str = "Std.Int64";     // was STD_LONG
pub const STD_DOUBLE: &str = "Std.Double";    // unchanged value
pub const STD_SINGLE: &str = "Std.Single";    // was STD_FLOAT
pub const STD_BOOLEAN:&str = "Std.Boolean";   // was STD_BOOL
pub const STD_CHAR:   &str = "Std.Char";      // unchanged value
```

`primitive_class_name(Value)` 映射保持 6 个 Value variant → 6 个 STD_*：

```rust
Value::I64(_)  => Some(STD_INT32),   // 当前 mapping 也是 I64 → "int"（z42 没区分 i32/i64 Value variant）
Value::F64(_)  => Some(STD_DOUBLE),
Value::Bool(_) => Some(STD_BOOLEAN),
Value::Char(_) => Some(STD_CHAR),
Value::Str(_)  => Some(STD_STRING),
Value::Array(_)=> Some(STD_ARRAY),
```

> **Note**：`Value::I64` → `STD_INT32` 看起来类型名错配（i64 → Int32），但 z42 当前的设计就是这样：VM `Value` enum 只有 I64 一个整数 variant，i8/i16/i32/i64/u8/...都用 `Value::I64` 存储；class FQN 由 compile-time 决定。`primitive_class_name` 仅在 IC 兜底 / "未知 method 的 retry" 路径用，作为受 Value variant 决定的"默认 primitive class"。这里的语义其实是 "Value::I64 的 default 路由到 Std.Int32"（不绑定 64 位整型；类型系统层面 i64 = `long` = `Std.Int64`，会通过 compile-time emit class name 直接路由）。

### Decision 4: Native binding rename 的粒度

**问题**：`__int_*` → `__int32_*` rename 是单纯 string key 替换，还是连 Rust 函数名一起 rename？

**决定**：**连 Rust 函数名一起 rename**，避免"string 说 int32 但函数说 int"的语义错配。三步同步：

1. `src/libraries/z42.core/src/Primitives/<File>.z42` 中 `[Native("...")]` 字符串改新名
2. `src/runtime/src/corelib/mod.rs` BUILTINS 表 `("__int32_parse", convert::builtin_int32_parse)` 同时改 string 和函数引用
3. `src/runtime/src/corelib/convert.rs` 函数定义 `pub fn builtin_int32_parse(...) -> ...` 改函数名
4. `src/runtime/src/corelib/convert_tests.rs` 测试中调用同步

`__char_*` 和 `__double_*` 不动（class 名拼写与 keyword 相同段，前缀已对齐）。

### Decision 5: 实施顺序

**问题**：13 stdlib + 2 C# 编译器 + 7 VM + 7 docs 改动，怎么排序避免中间态不可编译？

**决定**：**单 commit 原子提交**，但分 5 个内部 step 顺序写：

1. **Step 1**：扩 `TypeRegistry` 加 `StdlibClassName` 字段（默认值用旧 lowercase 名，不影响现有行为）+ TypeChecker switch 改为查 TypeRegistry。验证：build + test 全绿，行为不变。
2. **Step 2**：Rust `well_known_names.rs` 加新名常量但保留旧名常量为 alias（暂时双名）。
3. **Step 3**：stdlib 12 文件 rename + struct 名改 + Native binding 名改；同时改 `TypeRegistry.StdlibClassName` 字段值到新名；改 Rust `corelib/*.rs` builtin 函数名 + mod.rs BUILTINS 表。
4. **Step 4**：删 Rust well_known_names.rs 的旧常量（清理 alias）；更新 `primitive_class_name()` 用新常量名。
5. **Step 5**：跨包引用更新（`Array.z42` / `generic_inumber.z42`）+ 7 文档同步 + naming-conventions.md naming-conv-4 升级。

每个 step 完成后跑相关测试确认未破坏。最后一次性 commit。

> **不在 git 中保留 step1-4 的中间快照**：开发期可用 git stash / WIP commit 局部辅助，但最终 squash 成一个原子 commit `refactor(stdlib+vm): rename primitives to BCL PascalCase`。

## Implementation Notes

### TypeRegistry 扩展

`TypeEntry` 添加 `StdlibClassName` 字段（位置参数末尾，nullable 留余地）：

```csharp
public sealed record TypeEntry(
    string CanonicalName,
    string[] Aliases,
    IrType IrType,
    Z42Type? Z42Type,
    bool IsNumeric,
    bool IsIntegral,
    bool IsReference,
    (long Min, long Max)? LiteralRange,
    string? StdlibClassName);    // NEW
```

13 条 + `string` / `object` / `void` 数据更新。

### TypeChecker.Calls.cs 改动

`BindMemberCallOnUnknownTarget` 删硬编码 switch，改为 TypeRegistry 查表：

```csharp
private BoundExpr BindMemberCallOnUnknownTarget(
    MemberExpr mCallee, string tgtName, CallExpr call, TypeEnv env)
{
    var args = call.Args.Select(a => BindArgValue(a, env)).ToList();
    // Map keyword to stdlib class name; fall through for user types.
    var resolvedClassName = TypeRegistry.GetTypeEntry(tgtName)?.StdlibClassName ?? tgtName;
    // ... unchanged below
}
```

### BindMemberCallOnPrimitive 检查

`Z42PrimType("long")` 实例方法 dispatch 走 `BindMemberCallOnPrimitive`（line 136），调用图里某处会把 `Z42PrimType("long")` 映射到 lookup key。这条路径**也需要走 TypeRegistry**。Step 1 检查这里 —— 如果当前直接拼接 `"Std." + primName` 那就要改成 `TypeRegistry.GetTypeEntry(primName).StdlibClassName`。

### well_known_names.rs 改动

```rust
// ── Step 2 ──
pub const STD_INT32:   &str = "Std.Int32";
pub const STD_INT64:   &str = "Std.Int64";
pub const STD_DOUBLE:  &str = "Std.Double";   // unchanged value
pub const STD_SINGLE:  &str = "Std.Single";
pub const STD_BOOLEAN: &str = "Std.Boolean";
pub const STD_CHAR:    &str = "Std.Char";      // unchanged value

// ── Step 4 (cleanup) — delete: ──
// pub const STD_INT: &str = ...
// pub const STD_LONG: &str = ...
// pub const STD_DOUBLE: &str = ...   ← keep, value unchanged
// pub const STD_FLOAT: &str = ...
// pub const STD_BOOL: &str = ...
// pub const STD_CHAR: &str = ...     ← keep, value unchanged
```

Rename 后调用方（exec_vcall.rs / object.rs）一并更新。

### Native binding 命名表（完整 12 类型）

| 类型 | 旧 prefix | 新 prefix | 涉及的 builtin 名（举例）|
|------|---------|---------|----------------------|
| Boolean | `__bool_` | `__boolean_` | parse / equals / hash_code / to_string |
| Char | `__char_` | `__char_`（不变）| equals / hash_code / to_string / is_whitespace / to_lower / to_upper |
| SByte | `__i8_` | `__sbyte_` | parse |
| Int16 | `__i16_` | `__int16_` | parse |
| Int32 | `__int_` | `__int32_` | parse / equals / hash_code / to_string |
| Int64 | `__long_` | `__int64_` | parse |
| Byte | `__u8_` | `__byte_` | parse |
| UInt16 | `__u16_` | `__uint16_` | parse |
| UInt32 | `__u32_` | `__uint32_` | parse |
| UInt64 | `__u64_` | `__uint64_` | parse |
| Single | `__float_` | `__single_` | parse |
| Double | `__double_` | `__double_`（不变）| parse / equals / hash_code / to_string |

### 文档同步规则

- `naming-conventions.md` 把 `naming-conv-4` Deferred 段删除；§1 / §10 加新条款"primitive struct 名 PascalCase BCL 风格；keyword 是 source-level alias（参考 C# `int` ⟷ `System.Int32`）"
- 6 个 design doc 中的 `Std.int / Std.bool / Std.long / Std.float / Std.double / Std.char` 等 grep + replace 为 PascalCase

## Testing Strategy

### 单元测试

| 测试 | 覆盖点 |
|------|------|
| C# TypeChecker.PrimitiveDispatchTests | 13 keyword → PascalCase FQN 映射全覆盖 |
| Rust corelib::convert_tests | `builtin_int32_*` / `builtin_int64_*` / `builtin_sbyte_*` 等 12 类型 parse / equals 路径 |
| Rust corelib::tests::test_char_* | char builtin 名不变，确认 regression 测试通过 |
| 新增 Rust test::test_primitive_class_name | 6 Value variant → 6 STD_* 常量映射 |

### Golden / e2e

- `./scripts/test-all.sh --scope=full` 一次跑通（必经门禁）
- `./scripts/regen-golden-tests.sh` 跑通（zbc / zpkg fixture diff 显示 class name 字符串值变化符合预期）
- cross-zpkg 测试覆盖：`Std.Int32.ToString` 从 `z42.core` 跨包调用

### Spec scenario 直接验证

每条 spec.md 的 Scenario 都对应至少一个测试：
- "全部 12 primitive 都支持 keyword 调用形式" → `src/tests/primitives/keyword_dispatch/*.z42` 12 个 source.z42 + expected.txt
- "Std.Int32.MaxValue 等价" → 现有 generic_inumber test 改名后继续覆盖

### 静态检查

最后跑：
```bash
grep -rn "Std\.\(int\|bool\|char\|float\|double\|long\|i8\|i16\|u8\|u16\|u32\|u64\)\b" \
  src/libraries src/compiler src/runtime src/tests docs/design 2>/dev/null \
  | grep -v 'docs/spec/archive'
# 应为零（archive 历史记录除外）
```

## 风险与回退

| 风险 | 概率 | 缓解 |
|------|------|------|
| Step 1 引入的 TypeRegistry 字段使旧 codepath 行为漂移 | 中 | Step 1 给字段默认 lowercase 旧值，行为不变；测试逐步绿后再 Step 3 改值 |
| BindMemberCallOnPrimitive 路径未发现的硬编码 `"Std." + primName` | 中 | Step 1 grep 后手动检查所有 primitive name string 拼接位置 |
| Rust corelib 函数名 rename 漏掉一个 caller | 低 | cargo build 编译期捕获，0 风险漏改 |
| zbc fixture diff 出现 unexpected 字符串变化（除了 class name） | 低 | regen + 人工 diff review；如有其他变化停下来调研 |
| stdlib `.zpkg` regen 失败 | 中 | `regen-golden-tests.sh` 是已经稳定的工具；任何失败说明 Step 3 有未覆盖位 |
| 用户 in-the-wild `.z42` 测试代码引用 `Std.int.X()` 显式 FQN | 低 | grep 已确认 stdlib + 测试只有 1 处（`generic_inumber.z42`），其他全是 keyword 形式不受影响 |

如 GREEN 失败：回退到上一 step 起点（git stash / reset 单文件），逐 step 重做。
