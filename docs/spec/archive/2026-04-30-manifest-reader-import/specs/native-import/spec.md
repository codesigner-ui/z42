# Spec: Native import + manifest reader (C11a)

## ADDED Requirements

### Requirement: Lexer recognises `import` as Phase 1 keyword

#### Scenario: tokenises as Import
- **WHEN** Lexer 扫描 `import Counter from "numz42";`
- **THEN** 第 0 个 token = `TokenKind.Import`；后续 `from` 是 `TokenKind.Identifier`（contextual）

---

### Requirement: Parser captures NativeTypeImport into CompilationUnit

#### Scenario: 单个 import
- **WHEN** parse `import Counter from "numz42";`（顶层）
- **THEN** `cu.NativeImports` 含一项 `(Name="Counter", LibName="numz42")`

#### Scenario: 多个 import 顺序保留
- **WHEN**
  ```z42
  import A from "lib1";
  import B from "lib2";
  ```
- **THEN** `cu.NativeImports` 长度 2，顺序与源一致

#### Scenario: 缺 `from` 报错
- **WHEN** `import Counter "numz42";`
- **THEN** ParseException 含 "expected `from`"

#### Scenario: 缺 semicolon 报错
- **WHEN** `import Counter from "numz42"`（无 `;`）
- **THEN** ParseException

#### Scenario: import 与 using / namespace 同段共存
- **WHEN**
  ```z42
  namespace Demo;
  using Std.IO;
  import Counter from "numz42";
  ```
- **THEN** namespace + using + native import 都被正确捕获，互不干扰

---

### Requirement: NativeManifest.Read parses valid manifest JSON

#### Scenario: 合法 manifest → ManifestData
- **WHEN** Read 一份合法 manifest（含 abi_version=1 / module / version / library_name / types[]）
- **THEN** 返回 ManifestData with 字段值映射正确；types 列表完整

#### Scenario: types[] 含 fields/methods/trait_impls
- **WHEN** manifest type 含 1 method (name="inc", signature="(*mut Self) -> i64")
- **THEN** ManifestData.Types[0].Methods 含对应 MethodEntry

---

### Requirement: NativeManifest.Read raises E0909 on errors

#### Scenario: 文件不存在
- **WHEN** Read("/nonexistent/foo.z42abi")
- **THEN** NativeManifestException with Code=E0909；message 含路径

#### Scenario: JSON 解析失败
- **WHEN** Read 一份非 JSON 文件
- **THEN** NativeManifestException E0909

#### Scenario: abi_version 不匹配
- **WHEN** manifest 含 `"abi_version": 2`
- **THEN** NativeManifestException E0909 含 "abi_version 1 expected, got 2"

#### Scenario: 缺必需字段
- **WHEN** manifest 缺 `library_name`
- **THEN** NativeManifestException E0909 含字段名

## IR Mapping

不涉及 IR opcode；本 spec 仅引入 AST 顶层项 + reader 数据通路。

## Pipeline Steps

- [x] Lexer
- [x] Parser / AST
- [ ] TypeChecker — 不涉及（C11b）
- [ ] IR Codegen — 不涉及（C11b）
- [ ] VM — 不涉及

## Documentation

- error-codes.md E0909 启用
- interop.md / roadmap.md +C11a 行
- grammar.peg +import-stmt 产生式
