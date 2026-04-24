# Tasks: L3-G2.5 enum 约束 `where T: enum`

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** 新增 `where T: enum` 约束，要求类型实参必须是 enum 类型。
**原因：** z42 原生 enum 已就位；enum 是泛化 flags / 解析器 / 序列化工具的
  核心载体（`Parse<T: enum>(string) -> T`、`AllValues<T: enum>() -> T[]`）。
  是 L3-G2.5 约束范式补全的高优先级项。
**Scope 限制**：本次只做约束校验层。body 内 `T.Values` / `T.Parse` 等反射式
  enum 操作依赖 L3-R 运行时 type_args，不在本迭代。
**文档影响**：`docs/design/generics.md`（enum 约束语义）、`docs/roadmap.md`（G2.5 状态）。

## 任务

### 阶段 1：语义类型补全
- [x] 1.1 新增 `Z42EnumType : Z42Type` record（`z42.Semantics/TypeCheck/Z42Type.cs`）
- [x] 1.2 `SymbolCollector.ResolveType` 在 `_enumTypes.Contains` 时发射 `Z42EnumType`
- [x] 1.3 `SymbolTable.ResolveType` 同步补 enum 分支（TypeChecker 使用的主路径）

### 阶段 2：约束接入
- [x] 2.1 AST：`GenericConstraintKind.Enum = 1 << 3`
- [x] 2.2 Parser `ParseOneConstraint`：识别 `enum` 关键字 → 设置 Enum flag
- [x] 2.3 `GenericConstraintBundle.RequiresEnum` + `IsEmpty` 更新
- [x] 2.4 `TypeChecker.ResolveWhereConstraints`：kind flag → bundle 映射 + class/enum 互斥校验
- [x] 2.5 `IsEnumArg` 谓词 + `IsStructArg` 接纳 Z42EnumType + 校验路径插入

### 阶段 3：zbc / TSIG 元数据
- [x] 3.1 `ZbcWriter` flags bit `0x20` for RequiresEnum
- [x] 3.2 `ZbcReader`（C#）同步读取 0x20
- [x] 3.3 Rust VM `zbc_reader.rs` + `binary.rs` 同步读取 0x20
- [x] 3.4 Rust `ConstraintBundle` 新增 `requires_constructor` / `requires_enum` 字段
- [x] 3.5 TSIG：`ExportedTypeParamConstraint` + `ZpkgWriter` + `ZpkgReader` + `RehydrateConstraints`
- [x] 3.6 IrGen `BuildConstraintList` 传递 RequiresEnum 到 IrConstraintBundle

### 阶段 4：测试
- [x] 4.1 `TypeCheckerTests` 6 个 EnumConstraint 用例全绿
      （enum ✅ / class ✘ / struct ✘ / int ✘ / interface ✘ / class+enum 互斥 ✘）
- [x] 4.2 Golden test `84_generic_enum_constraint`（interp + jit 都绿）
- [x] 4.3 无新错误码（复用 TypeMismatch）

### 阶段 5：文档 + 归档
- [x] 5.1 `docs/design/generics.md` 增补 enum 约束小节 + 约束表更新
- [x] 5.2 `docs/roadmap.md` L3-G2.5 表格 + 已完成/后续迭代列表更新
- [x] 5.3 GREEN 全绿：547 编译器 + 162 VM (interp+jit)

## 备注

- 规范偏差记录：原计划只在 `SymbolCollector.ResolveType` 添加 enum 分支；
  实施中发现 `SymbolTable.ResolveType` 是独立实现（TypeChecker 主路径），
  补了同样的分支。结果：Z42EnumType 真正在 TypeChecker 层流通
- 互斥规则：`class + enum` 报错（enum 是值类型）；`struct + enum` 冗余但允许；
  `enum + new()` 允许；`enum + IXxx<...>` 允许但目前 enum 还不能 implements interface（L3-R）
- Rust VM 的 `ConstraintBundle` 之前缺 `requires_constructor` 字段（ctor 迭代漏补），
  这次一并补齐，并加 `requires_enum`
