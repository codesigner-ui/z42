# Tasks: Generic Attribute Syntax `[Name<TypeArg>]` (R4.B / A1)

> 状态：🟢 已完成 | 创建：2026-04-30 | 归档：2026-04-30
> 依赖：R1.C TIDX v=2 已就绪（ExpectedThrowTypeIdx 字段贯通）；R4.A TestAttributeValidator 已落地

## 进度概览

- [x] 阶段 1: AST 字段 + Parser 支持
- [x] 阶段 2: 编译期校验（E0913）
- [x] 阶段 3: IrGen 写入 TIDX
- [x] 阶段 4: 单元测试
- [x] 阶段 5: 文档同步
- [x] 阶段 6: 验证

---

## 阶段 1: AST 字段 + Parser 支持

- [x] 1.1 [src/compiler/z42.Syntax/Parser/Ast.cs](src/compiler/z42.Syntax/Parser/Ast.cs#L206) `TestAttribute` record 加 `string? TypeArg` 字段（在 `NamedArgs` 之前；更新构造调用）
- [x] 1.2 [src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs](src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs#L212) `TestAttributeNames` HashSet 加 `"ShouldThrow"`
- [x] 1.3 同文件 `ParseTestAttributeBody` 在 advance 过 attribute 名后插入 type-arg 解析（design.md Decision 5 代码片段）：
    - 接受 `<Identifier>` 单参
    - 拒绝 `<>`（空）→ ParseException
    - 拒绝 `<A, B>`（多参）→ ParseException
    - 拒绝 `<List<int>>`（嵌套）→ ParseException
- [x] 1.4 同文件返回 `TestAttribute` 时把 `typeArg` 传入新增字段
- [x] 1.5 检查所有 `new TestAttribute(...)` 调用点是否需要更新（grep）

## 阶段 2: 编译期校验（E0913）

- [x] 2.1 [src/compiler/z42.Semantics/TestAttributeValidator.cs](src/compiler/z42.Semantics/TestAttributeValidator.cs) 在 `ValidateFunction` 第一段 attribute 分类时收集 `hasShouldThrow` + `shouldThrowAttr` 引用
- [x] 2.2 加新检查段 "E0913 [ShouldThrow] type validation"：
    - **Rule 1**：`hasShouldThrow && shouldThrowAttr.TypeArg is null` → E0913 "type argument required"
    - **Rule 2**：`hasShouldThrow && TypeArg 非 null && type 在符号表中找不到` → E0913 "unknown type"
    - **Rule 3**：`hasShouldThrow && type exists && 不继承 Exception` → E0913 "must derive from Exception"
- [x] 2.3 加检查 "E0913 type arg on disallowed attribute"：遍历 `fn.TestAttributes`，对每个非 `ShouldThrow` 的 attribute，若 `TypeArg != null` → E0913
- [x] 2.4 扩展 E0914 "modifier requires primary"：把 `hasShouldThrow` 加入 `(hasSkip || hasIgnore)` 同组判断（`!hasTest && !hasBenchmark` 时报错）
- [x] 2.5 注：符号表 lookup —— 需要从 CompilationUnit 解析类型名到 ClassDecl，类似 `class X : Exception` base 解析路径。研究现有 base resolution（grep `Exception` in TypeChecker）确定 reuse 接口

## 阶段 3: IrGen 写入 TIDX

- [x] 3.1 [src/compiler/z42.Semantics/Codegen/IrGen.cs:253](src/compiler/z42.Semantics/Codegen/IrGen.cs#L253) 把硬编码 `ExpectedThrowTypeIdx: 0` 替换为：从 `fn.TestAttributes` 找 `ShouldThrow` attribute → 取 `TypeArg` → 加入 string pool → 1-based idx
- [x] 3.2 同时在 `flags` 中位运算设 `TestFlags.ShouldThrow`（grep `TestFlags` 找 C# 侧定义）
- [x] 3.3 缺 ShouldThrow 时保持 `ExpectedThrowTypeIdx: 0`，flag 不置位

## 阶段 4: 单元测试

- [x] 4.1 [src/compiler/z42.Tests/Parser/TestAttributeParserTests.cs](src/compiler/z42.Tests/Parser/TestAttributeParserTests.cs) NEW（如不存在）—— 6 个 parser scenario（spec.md 列表）
- [x] 4.2 [src/compiler/z42.Tests/Semantics/TestAttributeValidatorTests.cs](src/compiler/z42.Tests/Semantics/TestAttributeValidatorTests.cs) MODIFY —— 加 6 个 E0913 / E0914 scenario
- [x] 4.3 [src/compiler/z42.Tests/IR/TidxRoundtripTests.cs](src/compiler/z42.Tests/IR/TidxRoundtripTests.cs)（如不存在 NEW；存在 MODIFY）—— 1 个 round-trip：编译 `[Test] [ShouldThrow<TestFailure>] void f() {}` → ZbcReader 读 → 断言 `ExpectedThrowTypeIdx != 0` + 解析回 "TestFailure" + flags 含 SHOULD_THROW

## 阶段 5: 文档同步

- [x] 5.1 [docs/design/testing.md](docs/design/testing.md) 加 "R4.B Generic Attribute Syntax" 小节：
    - 语法 `[Name<TypeArg>]` 形态
    - 当前唯一用例 `[ShouldThrow<E>]`
    - 编译期写入 `TIDX.ExpectedThrowTypeIdx` 流程
    - runtime 比对延后到 A2 spec
- [x] 5.2 [docs/design/language-overview.md](docs/design/language-overview.md) 检查 attribute 语法段是否需更新（如有"attribute 不支持泛型"的过时陈述则修正）
- [x] 5.3 [docs/roadmap.md](docs/roadmap.md) M6 测试体系段：把 R4.B 从"未做"改为"已做（编译期）"，A2 / R3 完整版仍在 backlog

## 阶段 6: 验证

- [x] 6.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] 6.2 `cargo build --manifest-path src/runtime/Cargo.toml` 无错（Rust 侧无变更，但编译验证 zbc 兼容）
- [x] 6.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿（含本次新增 ~13 测试）
- [x] 6.4 `./scripts/test-vm.sh` 全绿（104/104 × 2，无回归）
- [x] 6.5 `./scripts/test-stdlib.sh` 全绿（5 passed / 2 skipped 不变；dogfood.z42 仍含 [Skip] 占位，A2 才替换）
- [x] 6.6 `./scripts/test-cross-zpkg.sh` 全绿（1/1 不变）
- [x] 6.7 spec scenarios 逐条覆盖确认（spec.md 列表）

## 备注

- 已知边界：`<Std.TestFailure>` dotted name 不支持（lexer 把 `.` 当独立 token，单 identifier 解不出）—— 用户必须用短名 + using 解析。如未来需支持，扩成 dotted ident 解析（`<a>.<b>.<c>` 拼字符串）
- Parser 中 `<` 在 attribute 上下文无歧义（attribute body 内紧跟 identifier 后）—— 不需 lookahead
- A2 spec（runner ShouldThrow runtime check + dogfood 替换）一旦 A1 落地立刻可起，依赖关系明确
