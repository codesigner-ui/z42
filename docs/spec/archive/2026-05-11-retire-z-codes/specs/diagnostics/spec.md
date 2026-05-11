# Spec: Retire Z#### Codes

## ADDED Requirements

### Requirement: Marshal NUL → stdlib exception type

VM 在 marshal `z42 string` 为 `*const c_char` 时若检测到 interior NUL，必须**抛出 `Std.InvalidMarshalException` z42 异常**（user-catchable），而不是用 anyhow! 错误字符串带 `Z0908:` 前缀。

#### Scenario: z42 user passes NUL-containing string through pinned block

- **WHEN** z42 code 写
  ```z42
  string s = "foo\0bar";
  pinned p = s {
      Std.Native.PassToCNative(p);  // 接受 *const c_char
  }
  ```
- **THEN** VM 抛出 `Std.InvalidMarshalException` 实例
- **AND** 异常 `Message` 字段包含 `"cannot pass z42 string ... as *const c_char: contains interior NUL"`
- **AND** 异常 `StackTrace` 字段定位到 marshal 触发位置
- **AND** z42 user code 可以 `catch (Std.InvalidMarshalException e)` 处理
- **AND** 异常对象的类型 FQ 名是 `Std.InvalidMarshalException`

#### Scenario: 不再带 Z0908 前缀

- **WHEN** 上述场景触发
- **THEN** 错误消息**不含** `"Z0908"` 字符串（前缀完全消失）
- **AND** 错误消息**不含**任何 Z#### 编号

### Requirement: Embedder-facing native errors stay as Rust errors

Native binding setup 阶段（z42 script 运行之前）的错误（原 Z0905 / Z0906 / Z0910）保留 Rust `anyhow!` 错误形态，但**移除** `Z####:` 前缀。

#### Scenario: Native type registration failure（原 Z0905）

- **WHEN** embedder 用错误 descriptor 调用 `z42_register_type`
- **THEN** 函数返回 Rust `Err(anyhow!(...))`
- **AND** 错误消息**不含** `"Z0905"` 字符串
- **AND** 错误消息保留原有诊断信息（如 `"descriptor.module_name is null"`）

#### Scenario: Native library load failure（原 Z0910）

- **WHEN** dlopen 失败或符号不存在
- **THEN** 返回 Rust `Err(anyhow!(...))` 不含 `Z0910` 字符串
- **AND** `error::Z0910` 常量从 `src/runtime/src/native/error.rs` 删除

### Requirement: Z catalog 基础设施全部删除

#### Scenario: Z.json 不再存在

- **WHEN** 检查仓库
- **THEN** `docs/error-codes/Z.json` 不存在
- **AND** `docs/error-codes/` 整个目录不存在

#### Scenario: Rust catalog 模块不再存在

- **WHEN** 检查 `src/runtime/src/`
- **THEN** `diagnostics/` 整个目录不存在
- **AND** `src/runtime/src/lib.rs` 不再有 `pub mod diagnostics;`
- **AND** `cargo build -p z42_vm` 通过

#### Scenario: C# RustErrorCatalog 不再存在

- **WHEN** 检查 `src/compiler/z42.Core/Diagnostics/`
- **THEN** `RustErrorCatalog.cs` 不存在
- **AND** `src/compiler/z42.Core/z42.Core.csproj` 不含 `<EmbeddedResource Include=".../Z.json" />`
- **AND** `dotnet build src/compiler/z42.slnx` 通过

### Requirement: z42c explain 收窄到 E####

`z42c explain` 工具继续支持 E#### compile-time 错误码（C# 内部 `DiagnosticCatalog`），不再支持 Z#### 查询。

#### Scenario: z42c explain E0401 工作不变

- **WHEN** user 运行 `z42c explain E0401`
- **THEN** 输出 E0401 的描述（来自 C# `DiagnosticCatalog`）
- **AND** 输出格式与本次重构前一致

#### Scenario: z42c explain Z0905 给出友好提示

- **WHEN** user 运行 `z42c explain Z0905`
- **THEN** 工具退出码非 0
- **AND** stderr 输出 friendly hint，提示 Z#### codes have been retired and exceptions are now first-class types

### Requirement: z42-vm --explain / --list-errors flag 删除

#### Scenario: z42-vm CLI 不再接受 --explain

- **WHEN** user 运行 `z42-vm --explain Z0905`
- **THEN** clap argument parser 报 "unknown argument: --explain"
- **AND** `--list-errors` 同样不存在
- **AND** `z42-vm --help` 输出不含 `--explain` / `--list-errors`

### Requirement: Std.InvalidMarshalException 类型存在

#### Scenario: stdlib 含 Std.InvalidMarshalException

- **WHEN** 用户 import stdlib 并查询 `Std.InvalidMarshalException`
- **THEN** 该类型存在
- **AND** 继承自 `Std.Exception`
- **AND** 有 `(string message)` 构造函数
- **AND** 有 docstring 描述（"Thrown when a z42 value cannot be marshalled to a native ABI type due to a contract violation, e.g. a z42 string containing interior NUL passed as `*const c_char`."）

## MODIFIED Requirements

### Requirement: VM throw site error message format

**Before**:
- VM throw site 使用 `anyhow!("Z####: <description>")` 形态
- 错误消息以 `Z####:` 前缀开始

**After**:
- VM throw site 使用 `anyhow!("<description>")` 不带 Z 前缀
- 或（user-catchable 场景）构造 stdlib 异常类型实例

### Requirement: z42c explain 命令支持的 code namespace

**Before**:
- 支持 E#### (C# 内部 catalog) + Z#### (RustErrorCatalog 加载 docs/error-codes/Z.json)

**After**:
- 仅支持 E#### (C# 内部 catalog)

## IR Mapping

无新 IR 指令；本 spec 不改 IR 层。

## Pipeline Steps

- [ ] Lexer — 不影响
- [ ] Parser / AST — 不影响
- [ ] TypeChecker — 不影响（仅 stdlib 加新异常类型，TypeChecker 走标准类发现）
- [ ] IR Codegen — 不影响
- [x] **VM interp** — marshal 路径改为构造 stdlib 异常实例并 throw
- [x] **C# DiagnosticCatalog** — 移除 RustErrorCatalog 注册
- [x] **z42-vm CLI** — 移除 `--explain` / `--list-errors`
- [x] **z42c CLI** — explain 命令路径不变（只是失去 Z#### lookup）
- [x] **stdlib** — 添加 `Std.InvalidMarshalException` 类型
