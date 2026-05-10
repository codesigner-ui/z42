# Spec: Native class synthesis (C11b — Path B1)

## ADDED Requirements

### Requirement: ManifestSignatureParser handles supported types

#### Scenario: primitives
- **WHEN** parse 签名字符串 `"i64"`、`"f64"`、`"bool"`、`"void"`
- **THEN** 返回对应的 z42 `NamedType("i64")` / `NamedType("f64")` / `NamedType("bool")` / `VoidType`

#### Scenario: Self return
- **WHEN** parse `"Self"`，type 名上下文 = `"Counter"`
- **THEN** 返回 `NamedType("Counter")`

#### Scenario: receiver pointer in first param
- **WHEN** parse `"*mut Self"` 或 `"*const Self"`
- **THEN** 返回标记为 receiver 的 sentinel（被合成器从 params 中剔除，不进入 z42 方法签名）

#### Scenario: unsupported type rejected
- **WHEN** parse `"*const c_char"` 或 `"String"` 或 `"Box<T>"` 或 `"Foo"`
- **THEN** 抛 NativeImportException with Code=E0916；message 含原始签名串

---

### Requirement: NativeImportSynthesizer 产生合成 ClassDecl

#### Scenario: 单 import 生成完整 ClassDecl
- **WHEN** `cu.NativeImports = [{Name="Counter", LibName="numz42"}]`，locator 返回的 manifest 含 `Counter` type（含 `alloc`(ctor) / `inc`(method, ret void) / `get`(method, ret i64) 三个 methods，library_name="numz42"）
- **THEN**
  - `cu.Classes` 末尾追加 1 个 `ClassDecl`，`Name == "Counter"`
  - `IsSealed == true`、`Visibility == Internal`、`Fields == []`
  - `ClassNativeDefaults != null`，`Lib == "numz42"`、`TypeName == "Counter"`、`Entry == null`
  - `Methods.Count == 3`，每个方法的 `Tier1Binding.Entry == <对应 manifest symbol>`

#### Scenario: ctor 方法正确识别
- **WHEN** manifest 中某 method `kind == "ctor"`、`name == "Counter"`、`symbol == "numz42_Counter_alloc"`
- **THEN** 合成的 FunctionDecl 满足
  - `Name == "Counter"`（与类同名）
  - `ReturnType` 是 `VoidType`（z42 ctor 约定）
  - `Tier1Binding.Entry == "numz42_Counter_alloc"`

#### Scenario: static 方法带 Static modifier
- **WHEN** manifest 中某 method `kind == "static"`
- **THEN** 合成 FunctionDecl 的 `Modifiers.HasFlag(Static) == true`

#### Scenario: 多个 import 顺序保留
- **WHEN** `cu.NativeImports = [A from "lib1", B from "lib2"]`
- **THEN** `cu.Classes` 末尾按顺序追加 ClassDecl A、B（顺序不变）

#### Scenario: 空 NativeImports 不改 cu
- **WHEN** `cu.NativeImports == null` 或为空
- **THEN** Synthesizer 跑过后 `cu.Classes` 完全不变

---

### Requirement: 错误路径

#### Scenario: manifest 文件找不到
- **WHEN** locator 抛 `NativeImportException(E0916, "manifest not found", path)`（或 NativeManifest.Read 抛 E0909）
- **THEN** 异常向上抛出，不写半成品 ClassDecl 进 cu.Classes

#### Scenario: import 的 type 不在 manifest
- **WHEN** `import Foo from "lib"` 但 manifest.Types 不含 `Foo`
- **THEN** 抛 NativeImportException with Code=E0916；message 含 "Foo"、库名、manifest 路径

#### Scenario: 同 import name 在两条 import 中不同 lib
- **WHEN**
  ```z42
  import Counter from "lib1";
  import Counter from "lib2";
  ```
- **THEN** 抛 NativeImportException E0916，message 提示 type-name 冲突

#### Scenario: 签名字符串无法解析
- **WHEN** manifest 某 method 的 ret 字段是 `"*const c_char"` (C11b 不支持)
- **THEN** 抛 NativeImportException E0916，message 含原签名串 + 方法名

---

### Requirement: 合成结果可被 TypeChecker / IrGen 一视同仁消化

#### Scenario: 端到端 lex → parse → synth → typecheck 通过
- **WHEN** 源代码：
  ```z42
  import Counter from "numz42";

  void Main() {
      var c = new Counter();
      c.inc();
      int v = c.get();
  }
  ```
  且 locator 提供合法 manifest
- **THEN** TypeChecker `HasErrors == false`；IrGen 为 `c.inc()` / `c.get()` 都 emit `CallNativeInstr`，`Tier1Binding.Lib == "numz42"`、`TypeName == "Counter"`、`Entry == "numz42_Counter_inc"` / `"numz42_Counter_get"`

#### Scenario: 用户调了 manifest 中没有的方法 → TypeCheck 报 UndefinedSymbol
- **WHEN** `c.no_such_method()` 而 manifest 不含此 method
- **THEN** TypeChecker 报 E0401（UndefinedSymbol），不是 E0916（合成已成功，方法不存在是普通 type-check 错误）

## IR Mapping

不引入新 IR opcode；合成产物完全复用 C9（`class-level-native-shorthand`）的 Tier1NativeBinding stitching + C6 (`extend-native-attribute`) 的 `EmitNativeStub` → `CallNativeInstr` 路径。

## Pipeline Steps

- [x] Lexer — 不涉及（C11a 已加 Import keyword）
- [x] Parser / AST — 不涉及（C11a 已加 NativeTypeImport / CompilationUnit.NativeImports）
- [x] **Synthesizer — 本 spec 新增**
- [ ] TypeChecker — 不改动（消费合成 ClassDecl 走既有路径）
- [ ] IR Codegen — 不改动（C9 stitching 已就位）
- [ ] VM — 不改动（C2–C10 dispatch 原样可用）

## Documentation

- error-codes.md +E0916 entry
- interop.md §10 Roadmap +L2.M13f / C11b 行 + Path B1/B2/C 选型说明
- roadmap.md +C11b 行
