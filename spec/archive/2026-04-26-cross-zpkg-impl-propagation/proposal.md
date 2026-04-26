# Proposal: 跨 zpkg `impl` 块传播（L3-Impl2）

## Why

L3-Impl1（已完成）让 `impl Trait for Type { ... }` 在同 CU 内工作：
SymbolCollector 把 impl 方法合并进 target class 的 `Methods` 字典 +
trait 加到 `_classInterfaces[target]`。

但**跨 zpkg 不可见**：

```z42
// 假想包 z42.numerics
using z42.core;

impl INumber<int> for int {
    public static int op_Add(int a, int b) { return a + b; }
    // ...
}
```

z42.numerics 编译时把 `op_Add` 合并进 imported `Std.int` 的 `_classes` 条目，
但 [`ExportedTypeExtractor.ExtractClasses`](src/compiler/z42.Semantics/TypeCheck/ExportedTypeExtractor.cs#L32)
跳过 imported classes（`if (sem.ImportedClassNames.Contains(name)) continue`），
所以 z42.numerics 的 zpkg 既没有 `int` 的导出（不该有），也没有"我给 int
追加了 `INumber<int>` 实现"的记录 → 下游消费者看不到。

VM 侧倒是能用（IrGen 给 impl 方法生成的函数符号 `<ns>.int.op_Add` 在 z42.numerics
zpkg 的 MODS 里，运行时 `using z42.numerics` 加载后 `func_index` 注册成功），
但 **TypeChecker 不通过**：消费者代码 `where T: INumber<int>` 检查 `int` 是否
满足约束时，z42.core 的 TSIG 说 `int` 没实现 `INumber` → 编译报错。

因此 L3-Impl2 关键不是 VM 调用问题，而是**让 impl 信息穿过 zpkg 边界
进入下游 TypeChecker**。

> 也注意一个隐藏 bug：[`IrGen.cs:132`](src/compiler/z42.Semantics/Codegen/IrGen.cs#L132)
> 用 `QualifyName(targetNt.Name)` 而非 `QualifyClassName`，跨包 impl
> 会把方法注册到错误的命名空间。本变更顺手修复（属"必须改产出端"的根因修复）。

## What Changes

### 设计要点

1. **新增 zbc IMPL section**：每个 z42 编译单元在 zpkg 里附带一个 IMPL section，
   记录"本包对外部 / 本地 target 的 impl 追加"
2. **ExportedTypeExtractor**：从 `cu.Impls`（不是 sem.Classes）抽出 impl records，
   写入 IMPL section
3. **ImportedSymbolLoader**：读取上游 zpkg 的 IMPL section，把 impl 方法合并进
   imported class 的 `Methods` 字典 + 把 trait 加进 `ClassInterfaces[target]`
4. **IrGen.cs:132 bug 修复**：`QualifyName` → `QualifyClassName`，让 impl 方法
   函数符号正确指向 target 的命名空间

### IMPL section schema (草案)

```
[Impl Count: ushort]
For each impl:
    [Target FQ name pool idx: uint32]   ← 例如 "Std.int"
    [Trait FQ name pool idx: uint32]    ← 例如 "Std.INumber"
    [Trait TypeArg Count: byte]
    For each trait type arg: [Type string pool idx: uint32]
    [Method Count: ushort]
    For each method: WriteMethodDef(...)  ← 复用 ExportedMethodDef 序列化
```

**注意**：方法 body **不在 IMPL section**，仍走 MODS section（与普通 class 方法一致）。
IMPL section 只承载"声明追加"。

### 关键不变量

- impl 方法 body 走 z42.numerics 自己的 MODS section（VM 侧 `func_index` 注册）
- IMPL section 只承载"target X 通过 trait Y 获得方法 M 的签名"声明
- 下游 ImportedSymbolLoader 在 Phase 2 之后做"impl merge pass"，与 base-class
  inheritance merge 同模式
- impl 方法在 target class 自身的 `Methods` 中**直接展开**（与 L3-Impl1 一致），
  避免下游每次 lookup 都要遍历 impl 列表
- 孤儿规则**继续宽松**（与 L3-Impl1 一致）：本变更不引入 Rust 风严格检查

## Scope

| 文件 | 变更类型 | 说明 |
|------|---------|------|
| `src/compiler/z42.IR/ExportedTypes.cs` | edit | 新增 `ExportedImplDef` record |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | edit | 新增 IMPL section 写入；version 0.7→0.8 |
| `src/compiler/z42.Project/ZpkgWriter.cs` | edit | 新增 IMPL section 序列化 |
| `src/compiler/z42.Project/ZpkgReader.cs` | edit | 解码 IMPL section |
| `src/compiler/z42.Semantics/TypeCheck/ExportedTypeExtractor.cs` | edit | 新增 `ExtractImpls(cu)` |
| `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` | edit | Phase 3 — 合并 impl 进 imported class |
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | edit | line 132 `QualifyName` → `QualifyClassName` |
| `src/runtime/src/metadata/binary.rs` | edit | 解码 IMPL section（VM 侧暂不消费，但格式要兼容） |
| `src/runtime/tests/golden/run/102_cross_zpkg_impl/` | add | golden test：临时多 zpkg 场景 |
| `src/compiler/z42.Tests/...` | add | 单元测试：IMPL section roundtrip + ImportedSymbolLoader merge |
| `docs/design/generics.md` | edit | extern impl 章节加 L3-Impl2 落地小节 |
| `docs/design/compiler-architecture.md` | edit | TSIG 章节加 IMPL section 描述 |
| `docs/roadmap.md` | edit | L3-Impl2 状态 ✅ |

## Out of Scope

- **VM 侧消费 IMPL**：VM 不需要读 IMPL section（方法 body 在 MODS，dispatch 走
  `func_index`）。VM 只需**能跳过** IMPL section（version bump 已迫使 regen，
  无兼容包袱）
- **孤儿规则收紧**：保留宽松规则，留给后续 Rust 风完整规则迭代
- **`extern` 方法 in impl**：永久禁止（已通过 `forbid-extern-in-impl` lock-in）
- **创建 z42.numerics 实际包**：本变更只验证机制，golden test 用临时 mock 包

## Open Questions

- [ ] IMPL section 是否需要 trait 的 TypeArgs（如 `INumber<int>` 的 `int`）？
  → **倾向：要**。否则消费者不知道 `int` 实现的是 `INumber<int>` 还是
  `INumber<long>`。trait 的 TypeArgs 已在 `ImplDecl.TraitType` 里
- [ ] 跨包 impl 同名方法冲突如何处理？
  - case A：z42.numerics 给 int 加 `op_Add`，z42.numerics2 也加 `op_Add`
  - case B：z42.numerics 给 int 加 `op_Add`，z42.core 的 int 已有 `op_Add`（不同签名）
  → **倾向**：first-wins + diagnostic（与 L3-Impl1 同 CU 冲突检查对齐）
- [ ] target 必须是 imported 的吗？
  → **不**。本地 target + 本地 impl 已经被 L3-Impl1 处理；本地 target 但
  消费者可能还想接收"我的 impl 给我的 target"以便下游再扩展 — 仍写 IMPL
- [ ] 同一 zpkg 多个 impl 块给同一 target 加同一 trait 怎么办？
  → 同 L3-Impl1 检查：dup method 报错

## Blocks / Unblocks

- **Unblocks**：
  - z42.numerics 包能给 z42.core 的 primitive 类型追加数值 trait
  - 任何"扩展包给 z42.core 的类型加新接口"模式（dependency injection 风）
  - z42.linq 等基于 IEnumerable<T> 的扩展包
- **Blocks**：无（独立变更）
