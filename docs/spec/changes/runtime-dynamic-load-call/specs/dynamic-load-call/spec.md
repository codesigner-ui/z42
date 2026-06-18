# Spec: 运行时动态加载 zpkg + 静态函数调用

## ADDED Requirements

### Requirement: 按逻辑名加载 zpkg（平台透明）

#### Scenario: 加载一个 packed zpkg（逻辑名）
- **WHEN** z42 代码调用 `Std.Runtime.LoadZpkg("z42.workload.ios")`，VM 通过 ZpkgResolver 或默认 filesystem 搜索找到对应 packed zpkg，且 zbc 版本与 VM 匹配
- **THEN** 其类型与函数被注册进 VM 全局表，后续按 FQN 解析可命中

#### Scenario: 默认 filesystem 优先级搜索
- **WHEN** 未注册 apphost ZpkgResolver，调用 `LoadZpkg("z42.workload.ios")`
- **THEN** VM 按顺序搜索：`.z42/workloads/z42.workload.ios.zpkg`（项目本地）→ `$Z42_HOME/runtimes/<ver>/workloads/z42.workload.ios/`（版本作用域）→ `$Z42_HOME/workloads/z42.workload.ios.zpkg`（全局），第一个命中则加载

#### Scenario: Apphost ZpkgResolver 优先于 filesystem
- **WHEN** apphost 注册了 ZpkgResolver（如 Android AssetResolver），调用 `LoadZpkg("z42.workload.android")`
- **THEN** VM 通过 resolver 获取 bytes，跳过默认 filesystem 搜索；bytes 解析为 zpkg 后正常注册

#### Scenario: 幂等
- **WHEN** 同一逻辑名被 `LoadZpkg` 调用两次
- **THEN** 第二次为 no-op，不报错、不重复注册

#### Scenario: 名称无法解析
- **WHEN** 名称在 resolver 和所有 filesystem 搜索路径均未找到
- **THEN** 抛 `RuntimeException`，消息含名称与搜索路径摘要

#### Scenario: 文件格式错误 / 版本不符
- **WHEN** 找到文件但不是合法 zpkg，或 zbc 版本与 VM 不匹配
- **THEN** 抛 `RuntimeException`，消息含名称与原因

### Requirement: 按 FQN 调用约定签名的静态函数

#### Scenario: 调用 (string[])->int 静态函数
- **WHEN** 已 `LoadZpkg` 的 zpkg 含 `public static int Foo.Run(string[] a)`，调用 `Std.Runtime.CallStatic("Foo.Run", new[]{"x","y"})`
- **THEN** 该函数以 `["x","y"]` 执行，其 `int` 返回值原样回传给调用方

#### Scenario: 返回值/参数透传
- **WHEN** `Foo.Run` 返回 `a.Length`（传入 3 个串）
- **THEN** `CallStatic` 返回 `3`

#### Scenario: FQN 未找到
- **WHEN** `CallStatic("No.Such.Fn", args)` 且无任何已加载/可懒加载 zpkg 提供它
- **THEN** 抛 `RuntimeException`（函数未找到）

#### Scenario: 签名不符（严格）
- **WHEN** 目标函数不是 `static (string[])->int`（参数/返回/static 任一不符）
- **THEN** 抛 `RuntimeException`（签名不匹配），不尝试通用 marshaling

#### Scenario: 被调函数抛异常
- **WHEN** `Foo.Run` 内部抛异常
- **THEN** 该异常沿调用边界传回 `CallStatic` 调用方（可被其 try/catch 捕获）

#### Scenario: interp / JIT 一致
- **WHEN** 同一 LoadZpkg + CallStatic 场景分别在 interp 与 JIT 后端运行
- **THEN** 返回值与副作用一致

## IR Mapping
无新 IR 指令 / 无 zbc 格式变更（纯 builtin + stdlib extern）。

## Pipeline Steps
- [ ] Lexer —— 无
- [ ] Parser / AST —— 无
- [ ] TypeChecker —— 无（stdlib extern 签名）
- [ ] IR Codegen —— 无
- [x] VM interp —— `__load_zpkg`（含 ZpkgResolver 钩子 + filesystem 搜索）/ `__call_static` builtin + 重入执行
- [x] VM JIT —— 重入调用走标准后端，interp/JIT 一致
- [x] stdlib —— `Std.Runtime.LoadZpkg(string name)` / `CallStatic(string, string[])->int`
