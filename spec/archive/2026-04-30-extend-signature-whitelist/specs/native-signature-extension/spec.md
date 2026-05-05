# Spec: Extend manifest signature whitelist (C11e)

## ADDED Requirements

### Requirement: c_char param maps to z42 string

#### Scenario: `*const c_char` 作为 param
- **WHEN** `ManifestSignatureParser.ParseParam("*const c_char", "Counter", knownTypes={}, firstParam=false, span)`
- **THEN** 返回 `(IsReceiver=false, Type=NamedType("string"))`

#### Scenario: `*mut c_char` 作为 param
- **WHEN** 同上但 sig 是 `*mut c_char`
- **THEN** 同样返回 `NamedType("string")`

#### Scenario: c_char 出现在 return 位置（暂禁）
- **WHEN** `ParseReturn("*const c_char", ...)` 或 `*mut c_char`
- **THEN** 抛 NativeImportException E0916，message 含 `c_char return` 和 `C11f`

---

### Requirement: 指针指向其他 import 的 type

#### Scenario: 已 import 的类型作为 param
- **WHEN** `ParseParam("*mut Regex", "Match", knownTypes={Regex, Match}, firstParam=false, span)`
- **THEN** 返回 `(IsReceiver=false, Type=NamedType("Regex"))`

#### Scenario: 已 import 的类型作为 return
- **WHEN** `ParseReturn("*const Regex", "Match", knownTypes={Regex, Match}, span)`
- **THEN** 返回 `NamedType("Regex")`

#### Scenario: `*mut Other` 与 `*const Other` 等价
- **WHEN** `ParseParam("*mut X", ...)` 和 `ParseParam("*const X", ...)` knownTypes 含 X
- **THEN** 都返回 `NamedType("X")`，无差别

#### Scenario: 指针指向**未** import 的类型
- **WHEN** `ParseParam("*mut Foo", "Counter", knownTypes={Counter}, firstParam=false, span)`
- **THEN** 抛 NativeImportException E0916，message 含 `Foo`、`import Foo from` 提示

---

### Requirement: 错误信息分类

#### Scenario: unknown-type 错误（指向未知类型）
- **WHEN** 触发场景如上一条 unknown
- **THEN** message 形如 `"manifest references native type \`Foo\` but no matching \`import Foo from "...";\` is in scope"`

#### Scenario: unsupported-shape 错误（结构不在白名单）
- **WHEN** sig = `Box<T>` 或 `Array<T>` 或 `[u8; 32]`
- **THEN** message 形如 `"manifest <position> type \`<sig>\` is not supported by C11e synthesizer (whitelist: ...)"`，且列出当前可用 imports

---

### Requirement: 端到端合成（含 c_char + 跨 import 类型）

#### Scenario: 合成方法含 c_char param
- **WHEN** manifest 中某 method `params: [{name:"self", type:"*const Self"}, {name:"name", type:"*const c_char"}], ret:"void"`
- **THEN** 合成的 FunctionDecl `Params == [Param("name", NamedType("string"), null, span)]`（receiver 已剔除）

#### Scenario: 合成方法返回另一 import 类型
- **WHEN** `import Regex; import Match;`，manifest `Match` 的 method `find: (*const Self) -> *mut Regex`
- **THEN** 合成的 `find` `ReturnType == NamedType("Regex")`；TypeChecker pass 全绿

#### Scenario: 引用未 import 的类型 → 错误
- **WHEN** `import Match`（不 import `Regex`）；manifest Match 含 `find: (*const Self) -> *mut Regex`
- **THEN** Synthesizer 抛 E0916，message 含 `Regex`、`import Regex from`

## IR Mapping

不引入新 IR opcode；c_char 走 C8 既有 Arena marshal `(Value::Str, SigType::CStr)`；指向其他 native 类型走 C11b 既有 receiver / Tier1 dispatch。

## Pipeline Steps

- [ ] Lexer — 不涉及
- [ ] Parser / AST — 不涉及
- [x] **Synthesizer — 本 spec 扩 ManifestSignatureParser**
- [ ] TypeChecker — 不改动
- [ ] IR Codegen — 不改动
- [ ] VM — 不改动

## Documentation

- error-codes.md E0916 触发清单 +unknown-type / unsupported-shape 区分
- interop.md §11.5 表格更新（C11e 入栏）+ Roadmap +C11e 行
- roadmap.md +C11e 行
