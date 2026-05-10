# Spec: namespace-resolution — using 必须激活才可见

## ADDED Requirements

### Requirement: prelude package 自动激活

#### Scenario: z42.core 内的类型默认可见
- **WHEN** 源码无任何 `using` 声明，但使用 `Object` / `Exception` / `int.Parse` 等 z42.core 中的类型
- **THEN** 编译通过，类型解析到 z42.core

#### Scenario: 非 prelude 包不自动激活
- **WHEN** 源码无 `using Std.IO;`，但调用 `Console.WriteLine(...)`
- **THEN** 编译错误 E0204（UnknownIdentifier："Console"），并提示"是否缺少 `using Std.IO;`?"

### Requirement: using 激活非 prelude 包

#### Scenario: 显式 using 激活
- **WHEN** 源码 `using Std.Collections;` + `new Queue<int>()`（Queue 在 z42.collections）
- **THEN** 编译通过

#### Scenario: 同 namespace 横跨 prelude 与非 prelude（prelude 类型）
- **WHEN** 源码无 `using Std.Collections;`，使用 `new List<int>()`（List 在 z42.core 的 Std.Collections）
- **THEN** 编译通过（z42.core 是 prelude，其全部 namespace 默认激活）

#### Scenario: 同 namespace 横跨 prelude 与非 prelude（非 prelude 类型）
- **WHEN** 源码无 `using Std.Collections;`，使用 `new Queue<int>()`（Queue 在 z42.collections）
- **THEN** 编译错误 E0204，提示"是否缺少 `using Std.Collections;`?"

### Requirement: 类型名冲突诊断

#### Scenario: 两个非 prelude 包同 (ns, name) 同时激活
- **WHEN** 源码 `using Foo;` + `using Bar;`，且 packageA 和 packageB 都在 namespace `Foo` 下声明 class `Util`
- **THEN** 编译错误 E0210（NamespaceCollision）："class `Foo.Util` is provided by both `packageA` and `packageB`; rename or restrict using"
- **AND** 不进入 first-wins 静默路径

#### Scenario: prelude 与非 prelude 同 (ns, name)
- **WHEN** prelude 包 z42.core 声明 `Std.Foo`，非 prelude 包 third-party 也声明 `Std.Foo`，且 third-party 被激活
- **THEN** 编译错误 E0210，提示 third-party 占用了 prelude 命名空间

### Requirement: 未解析 using 升级为错误

#### Scenario: using 指向不存在的 namespace
- **WHEN** 源码 `using NoSuch.Pkg;`
- **THEN** 编译错误 E0211（UnresolvedUsing）："using `NoSuch.Pkg`: no loaded package provides this namespace"
- **AND** 替代当前的 stderr 警告

### Requirement: 保留前缀软警告

#### Scenario: 第三方包声明 Std 子命名空间
- **WHEN** package `acme.utils` 声明 `namespace Std.Acme;`，被加载到工程
- **THEN** zpkg 加载阶段输出警告 W0212（ReservedNamespace）："package `acme.utils` declares reserved namespace `Std.Acme`; `Std` / `Std.*` is reserved for stdlib"
- **AND** 不阻断构建（warn-only，避免破坏外部包调试 / 第三方临时占用）

## MODIFIED Requirements

### Requirement: ImportedSymbolLoader 过滤语义

**Before:** `usings` 参数是 namespace 白名单字符串集合，调用方默认传 `allNs`，
使过滤 effectively no-op；额外硬编码 `allowedNs.Add("Std")` 模拟 prelude。

**After:**
- `Load(modules, packageOfModule, activatedPackages, preludePackages)` —
  参数显式区分两类，移除硬编码 Std 注入
- modules 按 (package, namespace, class) 三元组组织
- 仅 `package ∈ activatedPackages ∪ preludePackages` 的类才进入返回的
  ImportedSymbols
- 同 (namespace, class-name) 多 package 来源 → 写入返回值的 Collisions 列表，
  TypeChecker 后续报 E0210

## Pipeline Steps
- [ ] Lexer（无变化）
- [ ] Parser / AST（无变化，using 已有）
- [ ] TypeChecker（新诊断码 + 利用 ImportedSymbols.Collisions）
- [ ] PackageCompiler / SingleFileCompiler（caller 路径迁移）
- [ ] ImportedSymbolLoader（过滤 + 冲突检测）
- [ ] Diagnostics（E0210 / E0211 / W0212）
- [ ] Doc 同步
- [ ] Golden test 补全 using
