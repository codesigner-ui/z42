# Tasks: extern impl 核心机制（Change 1 of Y 方案）

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** 新增 Rust 风 `impl Trait for Type { ... }` 块语法，支持在
类型定义**之外**声明接口实现。Target 可以是用户 class/struct 或 primitive struct
（int/double/bool/char，已通过 primitive-as-struct 完成 struct 化）。Trait 可以
是本地或导入的 interface。impl 方法必须有 body（extern 绑定留给后续迭代）。

**Scope 限制**：
- 单 CU（同 zpkg 内）的 impl 合并
- 孤儿规则宽松（不做跨 zpkg 严格检查）
- impl 方法必须有 body；不接受 `extern [Native(...)]`
- 不含 TSIG Impls 字段

## 任务

### 阶段 1：AST + Parser ✅
- [x] 1.1 `ImplDecl(TraitType, TargetType, Methods, Span)` AST
- [x] 1.2 `CompilationUnit.Impls` 字段
- [x] 1.3 `TokenKind.Impl` Phase2 → Phase1；从 `IsPhase2ReservedKeyword` 移除
- [x] 1.4 `TopLevelParser` 顶层 `impl` 识别
- [x] 1.5 `ParseImplDecl`：拒绝 extern 方法（Change 1 限制）
- [x] 1.6 `SkipToNextDeclaration` 加 `TokenKind.Impl`
- [x] 1.7 3 个 Parser 单元测试（基础 / 泛型接口实参 / 多方法）

### 阶段 2：SymbolCollector 合并 impl ✅
- [x] 2.1 `SymbolCollector.Impls.cs` 新 pass `CollectImpls(cu)`
- [x] 2.2 target 解析接受 user class、user struct、primitive struct、imported class
- [x] 2.3 trait 解析要求 interface
- [x] 2.4 签名对齐检查（漏方法 / 参数数量不匹配）
- [x] 2.5 trait 合并到 `_classInterfaces`，方法合并到 `Z42ClassType.Methods`
- [x] 2.6 冲突检查：只与 target 直接声明方法冲突时报错（允许 shadow 继承方法）
- [x] 2.7 `E0413 InvalidImpl` 诊断码 + catalog 条目

### 阶段 3：TypeChecker 绑定 impl 方法体 ✅
- [x] 3.1 `BindImplMethods` + `TryBindImplMethods` 错误隔离包装
- [x] 3.2 method body 绑定环境：`this` = target；字段 + type params 可见

### 阶段 4：IrGen 输出 ✅
- [x] 4.1 `_funcParams` 额外收录 impl 方法签名
- [x] 4.2 module emit 遍历 impl 方法走 `EmitMethod(targetName, m)` 正常路径

### 阶段 5：测试 ✅
- [x] 5.1 `TypeCheckerTests` 7 个用例
      （基础 ✅ / 满足接口约束 ✅ / 泛型接口实参 ✅ / 非用户 target ✘ /
       漏方法 ✘ / 签名不匹配 ✘ / 与 class 方法重复 ✘）
- [x] 5.2 3 个 Parser 单元测试
- [x] 5.3 Golden test `86_extern_impl_user_class`（interp + jit 双绿）

### 阶段 6：文档 + 归档 ✅
- [x] 6.1 `docs/design/generics.md` 新增 "extern impl" 小节
- [x] 6.2 `docs/roadmap.md` L3-Impl1 ✅ + L3-Impl2 规划
- [x] 6.3 `Diagnostic.cs` / `DiagnosticCatalog.cs` 新增 E0413
- [x] 6.4 GREEN 全绿：561 编译器 + 164 VM (interp+jit)

## 备注

- **与 primitive-as-struct 的关系**：primitive-as-struct 提供 "primitive = struct"
  类型载体；extern impl 提供"在外部追加接口实现"机制。两者协同后
  （Change 1 之后接入 extern 方法）：
  - 给 struct int 加 INumber 可以写在 z42.numerics 的 impl 块里，而非修改 z42.core 的 Int.z42
  - 避免 z42.core ↔ z42.numerics 循环依赖
- **孤儿规则 Change 1 宽松**：当前只要 target / trait 在本 CU 可见即可。后续收紧到
  Rust 风完整规则（impl 必须与 Trait OR Target 同 zpkg）
- **冲突检查**：只对 `cu.Classes[targetName].Methods`（AST 直接声明）冲突报错，
  不对继承自基类（如 `Object.Equals`）的方法报错 — 允许 shadow
- **primitive target body 方法**：`impl IMyTrait for int { int M() { return 42; } }`
  语法上可写，对 `this` 的访问在 primitive 场景当前不产生实际字段访问 IR
  （primitive 无字段），body 内可以用 `this` 作为 primitive 值本身
- **pre-existing 限制**（不阻塞）：泛型体内 `t.M(x)` 调用 constraint 接口方法时，
  interface 内部 T 不会按 constraint 的 TypeArgs 替换。golden test 避开了这个
  问题；未来独立 issue 跟踪
