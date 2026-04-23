# Tasks: extern impl 基础设施（用户类）

> 状态：🟢 已完成 | 完成：2026-04-23

**变更说明：** 新增 Rust 风语法 `impl Trait for Type { ... }` 支持为用户类
/struct 在 class 头部之外追加接口实现。等价于 class 头部 `: Trait`
+ 方法实现的分离写法。为 Change 2（primitive + 清理 `PrimitiveImplementsInterface`
硬编码）搭基础。

**原因：** z42 现有"类型-接口实现"机制只允许 class 头部声明，无法追溯为
primitive / 外部类型扩展接口。这个限制导致 IComparable/IEquatable/INumber
的 primitive 桥接不得不走 `PrimitiveImplementsInterface` 硬编码后门。extern impl
是根本解：本 Change 建立语法 / AST / SymbolCollector / TypeChecker 的底层机制，
Change 2 在此之上接入 primitive 与完成硬编码清理。

**Scope 限制**（严格）：
- 语法：`impl <TraitType> for <TargetType> { <methods> }`
- TargetType：仅接受**同一 zpkg 内的 user class/struct**
- TraitType：可带类型实参
- 孤儿规则 Change 1 简化版：**impl 必须与 TargetType 同 zpkg**
- impl 方法可以有 body；暂**不**接受 `[Native(...)] extern`（归 Change 2）
- **不含**：primitive target、跨 zpkg impl、IMPL zbc section、TSIG Impls 字段
- **不碰** `PrimitiveImplementsInterface` 硬编码（归 Change 2）

## 任务

### 阶段 1：AST + Parser
- [x] 1.1 新 AST `ImplDecl(TraitType, TargetType, Methods, Span)`
- [x] 1.2 `TokenKind.Impl` 从 Phase2 提升到 Phase1；`IsPhase2ReservedKeyword` 移除 Impl
- [x] 1.3 `TopLevelParser` 顶层识别 + 新增 `ParseImplDecl`
- [x] 1.4 `CompilationUnit` 加 `Impls` 字段
- [x] 1.5 Parser 单元测试 3 个（基础 / 泛型接口实参 / 多方法）

### 阶段 2：SymbolCollector 合并 impl 到 class
- [x] 2.1 新文件 `SymbolCollector.Impls.cs` + `CollectImpls(cu)` pass
- [x] 2.2 同 zpkg 校验、trait 解析、接口签名对齐、漏方法 / 签名不匹配诊断
- [x] 2.3 合并：trait 加入 `_classInterfaces`，方法加入 `_classes[name].Methods`
      （仅与**直接声明**的 class 方法冲突时报错，继承自基类的名字可以被 shadow）
- [x] 2.4 新诊断码 `E0413 InvalidImpl`（单码覆盖 6 类场景，与 InvalidInheritance 模式一致）

### 阶段 3：TypeChecker
- [x] 3.1 `BindImplMethods(impl)` + `TryBindImplMethods` 包裹器
- [x] 3.2 impl 方法体在 target class scope 下绑定（this、字段、type params）
- [x] 3.3 约束校验自动生效（walks `_classInterfaces`）

### 阶段 4：IrGen
- [x] 4.1 `_funcParams` 额外收录 impl 方法
- [x] 4.2 module emit 遍历 impl 方法作为 target class 的方法 → IrFunction
      （同一 `EmitMethod` 路径，vtable 自然包含）

### 阶段 5：测试
- [x] 5.1 `TypeCheckerTests` 新增 7 个 ImplBlock 用例（基础 ✅ /
      SatisfiesInterfaceConstraint ✅ / 泛型接口实参 ✅ /
      NonUserTarget ✘ / MissingMethod ✘ / SignatureMismatch ✘ / DuplicateMethod ✘）
- [x] 5.2 `ParserTests` 新增 3 个 ImplDecl 用例
- [x] 5.3 Golden test `86_extern_impl_user_class`（interp + jit 双绿）

### 阶段 6：文档 + 归档
- [x] 6.1 `docs/design/generics.md` 新增 "extern impl" 小节
- [x] 6.2 `docs/roadmap.md` L3-G2.5 表 / Change 1 ✅ / Change 2 规划行 / INumber 依赖说明
- [x] 6.3 新诊断码写入 `Diagnostic.cs` / `DiagnosticCatalog.cs`
- [x] 6.4 GREEN 全绿：559 编译器 + 164 VM (interp+jit)

## 备注

- 解析歧义：`impl X for Y` 中 `for` 复用 `TokenKind.For`，顶层上下文不冲突
- 孤儿规则 Change 1 仅做"同 zpkg"一半；Change 2 放开到完整 Rust 规则
- Change 1 impl 方法必须有 body；`extern` + `[Native]` 形态归 Change 2（primitive 需要）
- TSIG 无需扩展：合并后 class 的 interfaces 通过现有机制导出
- 方法名冲突检查只针对 class **直接声明**的方法；继承自基类（如 `Object.Equals`）
  可以被 impl 方法 shadow —— 否则任何实现 `IEquatable` 的类都会冲突
- 发现一处 pre-existing 限制（不阻塞本 Change）：泛型体内 `t.M()` 调用
  constraint 接口方法时，interface 内部 T 不会被 constraint 的 TypeArgs 替换
  （e.g. `where T: IMatches<int>` + `t.MatchesValue(n)` 期望参数被当成 T 而非 int）。
  golden test 避开了这个限制；后续独立 issue 跟踪
