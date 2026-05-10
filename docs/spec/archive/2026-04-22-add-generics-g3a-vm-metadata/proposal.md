# Proposal: L3-G3a — VM 约束元数据 + 加载时校验

## Why

L3-G2 / G2.5 把约束信息留在编译器内存中，**不**写入 zbc，VM 完全不知晓。后果：

1. **untrusted zbc 风险**：手工构造的 zbc 可跳过 TypeChecker，VM 无从检测
2. **反射前置阻塞**：L3-G3b 要暴露 `type.Constraints` / `t is IComparable<T>`，没元数据无从实现
3. **跨 zpkg 校验阻塞**：L3-G3d 依赖 TSIG section 携带约束，让消费方 TypeChecker 做跨模块校验
4. **关联类型阻塞**：L3-G3c 关联类型的消解需要运行时元数据辅助
5. **用户承诺**：2026-04-22 对话明确要求 VM 后续要加验证，已记录在 roadmap

L3-G3a 补齐元数据管道：写入 zbc → VM 读取 → 加载时做结构校验（constraint 引用的 class/interface 必须存在）。不引入运行时 Call/ObjNew 时的动态校验（代码共享下 type_arg 不可得）；留给 L3-G3b 反射 API 和 L3-G3c/d 按需使用。

## What Changes

### zbc 二进制格式（version 0.4 → 0.5）

**SIGS section** 每个 type_param 追加约束信息：
```
per tp:
  name_idx: u32                  (已有)
  + constraint_flags: u8         (新增)
    bit 0: RequiresClass
    bit 1: RequiresStruct
    bit 2: HasBaseClass
    bit 3..7: reserved
  + [if bit 2] base_class_name_idx: u32
  + interface_count: u8
  + interface_name_idx[]: u32 × interface_count
```

**TYPE section** 同样扩展（泛型类的 type_param 也带约束）。

**TSIG section** 未来 L3-G3d 再扩展（本次不动），TypeChecker 跨 zpkg 校验仍走 compiler 内存链路。

### C# 编译器

- `IrFunction` / `IrClassDesc` 新增 `TypeParamConstraints: List<IrConstraintBundle>?`
- `IrConstraintBundle(RequiresClass, RequiresStruct, BaseClass?, Interfaces)` 与 `GenericConstraintBundle` 同构
- `IrGen` 从 TypeChecker 的 `_funcConstraints` / `_classConstraints` 拷贝到 IrFunction/IrClassDesc
- `ZbcWriter` 写入；`ZbcReader` 读取；`ZasmWriter` 可读文本输出
- `ZpkgReader.ReadSigsSection` 同步跳过或解析

### Rust VM

- `bytecode.rs`：`Function.type_param_constraints: Vec<ConstraintBundle>`（与 `type_params` 对齐）；`ClassDesc.type_param_constraints` 同
- `zbc_reader.rs` / `binary.rs`：解码新字段
- `loader.rs`：
  - `TypeDesc.type_param_constraints` 字段
  - `build_type_registry` 拷贝 ClassDesc 约束到 TypeDesc
  - **加载时验证 pass**：遍历所有 constraint，确认 class/interface 名字都在 type_registry 里存在；未知引用 → `bail!("InvalidConstraintReference")`

### 运行时校验（本次不做）

- ObjNew / 泛型函数 Call 时校验 type_args 实现约束 — 代码共享下 type_args 不可得 → **留给 L3-G3b/c** 配合反射或显式 `TypeDesc` 传递
- 当前 TypeChecker 编译期校验在 trusted 管道下足够；untrusted zbc 校验等 L3-G3b 反射上线后统一推进

## Scope

| 文件/模块 | 变更 |
|-----------|------|
| `z42.IR/IrModule.cs` | `IrConstraintBundle` record + `IrFunction.TypeParamConstraints` + `IrClassDesc.TypeParamConstraints` |
| `z42.IR/BinaryFormat/ZbcWriter.cs` | VersionMinor 4 → 5；SIGS / TYPE 写入约束字段 |
| `z42.IR/BinaryFormat/ZbcReader.cs` | 读取约束字段 |
| `z42.IR/BinaryFormat/ZasmWriter.cs` | `.constraints T: Foo + IBar` 可读输出 |
| `z42.Project/ZpkgReader.cs` | SIGS 扫描时同步跳过新增字段（保持向前兼容） |
| `z42.Semantics/Codegen/IrGen.cs` | 把 `_funcConstraints` / `_classConstraints` → IR 对象 |
| `z42.Tests/ZbcRoundTripTests.cs` | 新增 3-4 round-trip 测试 |
| `z42.Tests/TypeCheckerTests.cs` | 无改动（TypeChecker 行为不变） |
| `src/runtime/src/metadata/bytecode.rs` | `ConstraintBundle` struct + Function/ClassDesc 新增字段 |
| `src/runtime/src/metadata/zbc_reader.rs` | 解码约束 |
| `src/runtime/src/metadata/binary.rs` | `decode_type_section` 同步 |
| `src/runtime/src/metadata/loader.rs` | TypeDesc 新增字段 + build_type_registry 拷贝 + 加载时 verify pass |
| `src/runtime/src/metadata/merge_tests.rs` | fixture 更新 |
| `src/runtime/tests/metadata/constraint_tests.rs` | 新增：loader 读取 + verify pass 覆盖 |
| `docs/design/ir.md` | zbc 格式小节补充约束字段布局 |
| `docs/design/generics.md` | L3-G3a 状态更新 |
| `docs/roadmap.md` | L3-G 进度表 G3a → ✅ |

## Out of Scope

- **运行时 Call/ObjNew 校验**：代码共享下 type_args 不可得；等 L3-G3b 反射机制出来后再决定如何拿 type_args 并 enforce
- **TSIG section 约束扩展**：跨 zpkg TypeChecker 消费，留给 L3-G3d
- **反射 API**：`type.Constraints` / `t is IComparable<T>`，留给 L3-G3b
- **关联类型**：L3-G3c
- **格式向后兼容**：本次直接 bump version，所有 zbc 重新生成（dotnet test + regen-golden-tests.sh + build-stdlib.sh 自动覆盖）

## Open Questions

- [ ] zbc version 策略：bump minor（0.4 → 0.5）+ 硬要求 reader 匹配？还是 flag 位支持新旧共存？
  - **决策**：bump minor，reader 严格匹配；仓库里所有 zbc 一次性重生成（golden + stdlib zpkg）
- [ ] 加载时 verify pass 是否默认开启？
  - **决策**：默认开启；未来可通过 `--trust` flag 跳过（L3-G3+ 加）
- [ ] 约束引用的 class/interface 若来自 lazy-loaded zpkg（如 Std.IComparable），加载时还不可见怎么办？
  - **决策**：verify pass 对 "未找到但以 `Std.` 开头" 的引用放行（lazy loader 会按需加载）；其他未知引用立即报错
