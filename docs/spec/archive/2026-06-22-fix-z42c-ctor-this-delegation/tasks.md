# Tasks: fix-z42c-ctor-this-delegation

> 状态：🟢 已完成 | 完成：2026-06-22 | 创建：2026-06-22
> 子系统：`z42c`
**变更说明：** z42c 把构造器 `: this(args)` 委托（同类 ctor 链）误编为 `: base(args)`（基类 ctor 调用）——parser 消费 `this`/`base` 关键字后**丢弃区分**，TypeChecker `_bindMethodBody` 一律解析基类 ctor。`new C()`（默认 ctor 委托 `: this(W,S)`）→ 调不存在的基类（Object）N-arg ctor → 运行期 `VCall: expected object, got Null` / 字段保持默认值。
**原因：** replace-csharp-compiler S3 dogfood 暴露：z42c-built `z42.test` Bencher 默认构造 `Bencher() : this(10,100)` → warmup+samples=0 → `test_bencher_default_runs_warmup_plus_samples` 断言 `expected 110 but got 0`。
**文档影响：** docs/design/compiler/self-hosting.md（z42c 已支持特性表 + S3 bug 记录）。

## 根因
- `Parser._parseMethodTail`：`if (Identifier) { advance(); } // base / this` —— 消费关键字但不记录是 base 还是 this。
- `MethodDecl` 只有 `BaseArgs/BaseArgCount`，无 this/base 区分位。
- `TypeChecker._bindMethodBody`：`BaseArgCount > 0` 时只解析 `curCls.BaseName` 的 ctor。
- 镜像 C#：`Ast.cs` 有 `BaseCtorArgs` + `ThisCtorArgs`（互斥）；`FunctionEmitter` `: this(...)` 解析同类 ctor key、`: base(...)` 解析基类。

## 修复（4 文件）
- [x] 1.1 `z42c.syntax/src/Decl.z42`：`MethodDecl` 加 `bool IsThisInit` 字段 + 构造器参数。
- [x] 1.2 `z42c.syntax/src/Parser.z42`：`_parseMethodTail` 捕获 `this` → `isThisInit=true`，传入 MethodDecl。
- [x] 1.3 `z42c.semantics/src/IrGen.z42`：3 处合成 getter/setter MethodDecl 传 `false`（非 ctor）。
- [x] 1.4 `z42c.semantics/src/TypeChecker.z42`：`_bindMethodBody` 按 `IsThisInit` 选 TargetCls（this→curCls，base→baseCls）+ targetName/ctorKey。
- [x] 1.5 回归单测 `z42c.semantics/tests/codegen/codegen_tests.z42`：`test_ctor_this_delegation`（`C() : this(5)` → `call @C.C$1(%0,%1)`）—— **codegen 单测 48/48 pass**。

## 验证
- [x] 2.1 codegen 单测 `test_ctor_this_delegation` pass（golden 实测：`fn @C.C$0(1) -> void {... %2 = call @C.C$1(%0, %1) ...}`）+ `test_ctor_lowering`(base) 不回归。
- [x] 2.2 div-by-zero oracle（real z42vm，z42c-compiled）：`new Foo() : this(10,100)` → A=10/B=100 → 100/ok clean exit 0。
- [x] 2.3 z42c 自身源码无 `: this(...)` ctor 委托 → byte-identical 7/7 安全（fix 对 z42c 7 包是 no-op）。
- [x] 2.4 full S3 gate 含 compiler-z42 byte-identical + z42c-built Bencher test → ✅ 全绿（`test_bencher_default_runs_warmup_plus_samples` pass + compiler-z42 7/7 + 17 units 含 test_ctor_this_delegation；与 S3 翻转合并验证）。

## 备注
- byte-identical-safe：与本会话前序 z42c fix 同模式（仅修复 z42c 不支持/误编的语言特性，z42c 自身不用 → 7 包 no-op）。
- C# bootstrap 早已正确支持（参考实现），本 fix 让 z42c 对齐。
