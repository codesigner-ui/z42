# Tasks: Auto-property 语法实现

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：lang (parser + typecheck + stdlib)

## 进度概览
- [x] 阶段 0: 调研验证 — IsFieldDecl lookahead 含 LBrace；BindMemberExpr 在 TypeChecker.Exprs.cs:541；BindAssign 含 IndexExpr setter dispatch 模板
- [x] 阶段 1: Parser ParseAutoPropAccessors + SynthesizeClassAutoProp + SynthesizeInterfaceAutoProp helpers
- [x] 阶段 2: 替换 3 处 SkipAutoPropBody（class body / interface body / extern property）
- [x] 阶段 3-4: TypeChecker BindMemberExpr 识别 getter（class/instantiated/interface 三处）+ BindAssign 加 property 路径 + TryFindPropertySetter helper
- [x] 阶段 5-6: golden tests 97_auto_property_class + 99_interface_property
- [x] 阶段 7: stdlib IEnumerator.Current 升级回 `T Current { get; }` 形式
- [x] 阶段 8: docs/design/properties.md + iteration.md 同步

---

## 阶段 0: 调研验证

- [ ] 0.1 grep `IsFieldDecl` / `IsAutoPropDecl` lookahead 当前实现
- [ ] 0.2 找到 BindMemberAccess 的具体函数（TypeChecker 文件）
- [ ] 0.3 确认 BindAssignment 的目标类型识别路径
- [ ] 0.4 验证 FieldDecl 数据结构是否含"是否 backing"标志（有则直接复用）
- [ ] 0.5 调查 SymbolCollector 处理 FieldDecl 的 visibility（Private 字段
  外部代码访问的现行行为）

## 阶段 1: Parser 新增 helpers

- [ ] 1.1 `TopLevelParser.Helpers.cs` 新增 `ParseClassAutoProp`：
  - 解析 `{ get; [set;] }` 块（accessor 顺序无关，至少含 `get`）
  - 合成 `FieldDecl __prop_<Name>: <type>`（private）
  - 合成 `FunctionDecl get_<Name>` 含 body `return this.__prop_<Name>;`
  - 仅当 `set;` 存在合成 `FunctionDecl set_<Name>(value)` 含 body
    `this.__prop_<Name> = value;`
- [ ] 1.2 新增 `ParseInterfaceProp`：纯 method signature 列表（无 body）
- [ ] 1.3 新增 `ParseExternProp`：extern method signature（同 interface
  但带 extern modifier；extern body 即无 body 标记，与现有 extern 方法
  路径一致）

## 阶段 2: 替换 SkipAutoPropBody 调用点

- [ ] 2.1 `TopLevelParser.cs:258` interface body：
  - 原：`if (LBrace && Peek(1).Text == "get") SkipAutoPropBody + continue`
  - 新：调 `ParseInterfaceProp(ref cursor, ...)` → 添加方法签名
- [ ] 2.2 `TopLevelParser.cs:402` class body field 后接 `{`:
  - 原：`if (LBrace) SkipAutoPropBody`
  - 新：判断是 auto-property（已经吃了 `<vis> <type> <name>`，cursor 在 `{`）
    → 调 `ParseClassAutoProp` → 添加 backing field + getter/setter 到 fields/methods
  - 注意：当前外层 `if (IsFieldDecl(cursor))` 后开始解析；auto-property
    被识别为 field 但有 `{` 后缀。需要拆分：
    - 若 `=`/`;`：普通 field
    - 若 `{`：auto-property → 不创建 fInit field，转 ParseClassAutoProp
- [ ] 2.3 `TopLevelParser.cs:521` extern property：
  - 原：`if (isExtern && LBrace) SkipAutoPropBody + return new FunctionDecl(name, [], ...)`
  - 新：调 `ParseExternProp` → 返回 IEnumerable<FunctionDecl>（getter +
    optional setter）；caller 处理多函数返回
- [ ] 2.4 删除（或保留 dead code）`SkipAutoPropBody` 函数

## 阶段 3: TypeChecker 成员访问 → 方法调用

- [ ] 3.1 `BindMemberAccess`（具体位置阶段 0.2 确认）：
  - 在原字段查找前先查 method `get_<Member>`（`Params.Count == 0`）
  - 命中 → 返回 `BoundCall(Virtual, target, qualClass, get_Name, [], getter.Ret, span)`
- [ ] 3.2 同步处理 `target.Type` 是 InstantiatedType / interface 等场景

## 阶段 4: TypeChecker 赋值 → setter 调用

- [ ] 4.1 `BindAssignment`（或对应位置）：
  - target 是 MemberAccessExpr 时，先查 setter method `set_<Member>`
  - 命中 → 返回 `BoundExprStmt(BoundCall(Virtual, target, qualClass, set_Name, [value]))`
- [ ] 4.2 readonly property 写入报错：尝试赋值 → `set_<Name>` 不存在 →
  fallback 到字段查找；若 backing field 也是 private 不可访问 → 报清晰错误
  （建议错误码：`property is read-only`）

## 阶段 5: 单元测试

新建或扩展 `src/compiler/z42.Tests/PropertyTests.cs`：

- [ ] 5.1 `Parser_ClassAutoProperty_DesugarsToFieldAndAccessors`
- [ ] 5.2 `Parser_ClassReadonlyAutoProperty_OnlyGetter`
- [ ] 5.3 `Parser_InterfaceProperty_DesugarsToMethodSignatures`
- [ ] 5.4 `Parser_ExternProperty_DesugarsToExternMethod`
- [ ] 5.5 `Parser_AutoPropertyMustHaveGet` (set-only 报错)
- [ ] 5.6 `TypeChecker_PropertyRead_BindsToGetterCall`
- [ ] 5.7 `TypeChecker_PropertyWrite_BindsToSetterCall`
- [ ] 5.8 `TypeChecker_ReadonlyPropertyAssign_ReportsError`

## 阶段 6: Golden tests

- [ ] 6.1 `run/97_auto_property_class`：用户类 multiple property + read/write
- [ ] 6.2 `run/98_auto_property_readonly`：readonly property + 编译错误用例
  (放在 `tests/golden/errors/` 而非 `run/`，按现有 errors test 形式)
- [ ] 6.3 `run/99_interface_property`：interface 含 property，class implement，
  调用走 VCall

## 阶段 7: stdlib 升级

- [ ] 7.1 `src/libraries/z42.core/src/IEnumerator.z42`：
  - 把 `T Current()` 改回 `T Current { get; }`
  - 删除"property 不支持，退化为方法形式"备注
- [ ] 7.2 `./scripts/build-stdlib.sh` 验证 stdlib 编译通过
- [ ] 7.3 `./scripts/regen-golden-tests.sh` 重生 zbc

## 阶段 8: 文档同步 + 归档

- [ ] 8.1 新增 `docs/design/properties.md`：
  - 使用者视角语法：class auto-property / readonly / interface / extern
  - desugar 规则（backing field + getter/setter）
  - `__prop_<Name>` 命名约定
- [ ] 8.2 `docs/design/language-overview.md` 加 property 章节链接
- [ ] 8.3 `docs/design/iteration.md` 更新：IEnumerator.Current 已恢复 property 形式
- [ ] 8.4 `docs/design/compiler-architecture.md` 在 Pratt / Parser 章节附近
  附 auto-property desugar 短小说明
- [ ] 8.5 GREEN 验证：dotnet test / test-vm.sh / cargo test 全绿
- [ ] 8.6 tasks.md 状态 → `🟢 已完成`
- [ ] 8.7 归档 + commit + push（scope `feat(parser+typecheck+stdlib)`）

## 备注

实施变更：

- **Field 优先于 property**（修订 spec D3）：
  原 spec 写 "property 优先"。实施时改为 "field 优先于 property" — 即先查
  `Fields[X]`，未命中再查 `Methods[get_X]`。理由：避免劫持 stdlib 的 String.Length
  等已有特殊路径；auto-property 合成 backing field `__prop_X` 不与 property
  名 `X` 冲突，效果上无差别但更安全。
- **String.IsEmpty body-property 形式不支持**：stdlib 唯一一处 body-property
  `bool IsEmpty { get { ... } }` 改成方法 `bool IsEmpty()`。spec out-of-scope
  的"自定义 body property"按 method 形式 surface，未来独立变更补齐。
- **extern property 仅 readonly**：当前 stdlib 无 extern setter use case；
  setter 报清晰错误 "extern property setter is not supported"。
- **TypeChecker 三个分支同步加 property 路径**：Z42ClassType / Z42InstantiatedType
  / Z42InterfaceType 各自的 BindMemberExpr 都加 `get_<X>` lookup（保持
  generic instantiated 的 type substitution）。
- **测试覆盖**：97_auto_property_class（class get-set + readonly）+
  99_interface_property（interface property + 实现）。新增 2 个 golden。
- **Wave 2 后插完成**：IEnumerator.Current 升级回 C# 标准 property 形式。
