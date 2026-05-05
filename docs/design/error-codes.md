# z42 Diagnostic Codes

All error, warning, and info codes emitted by the z42 compiler.

Use `z42c --explain <code>` to view the full description and an example in the terminal.
Use `z42c --list-errors` to print a compact summary of all codes.

The canonical source of truth is [`DiagnosticCodes.cs`](../../src/compiler/z42.Compiler/Diagnostics/Diagnostic.cs) (code constants) and [`DiagnosticCatalog.cs`](../../src/compiler/z42.Compiler/Diagnostics/DiagnosticCatalog.cs) (descriptions + examples).

---

## Z01xx — Lexer

| Code   | Title                         | When it occurs |
|--------|-------------------------------|----------------|
| Z0101  | Unterminated string literal   | A `"..."` or `'...'` literal is never closed |
| Z0102  | Invalid escape sequence       | `\q`, `\p`, or other unrecognized `\` sequence |
| Z0103  | Invalid numeric literal       | `0x` with no digits, malformed float exponent, etc. |

---

## Z02xx — Parser / Syntax

| Code   | Title                    | When it occurs |
|--------|--------------------------|----------------|
| Z0201  | Unexpected token         | Parser sees a token it cannot use at this position |
| Z0202  | Expected token           | A required token (`;`, `)`, `{`, …) is missing |
| Z0203  | Unexpected end of file   | File ends before a construct is complete |
| Z0204  | Missing return type      | Function declaration has no return type (not even `void`) |
| Z0205  | Ambiguous expression     | Expression cannot be parsed unambiguously |

---

## Z03xx — Feature Gates

| Code   | Title                         | When it occurs |
|--------|-------------------------------|----------------|
| Z0301  | Language feature not enabled  | A gated syntax (e.g. lambdas) is used without enabling it |

---

## Z04xx — Type Checker

| Code   | Title                            | When it occurs |
|--------|----------------------------------|----------------|
| Z0401  | Undefined symbol                 | Variable / function / type used before declaration |
| Z0402  | Type mismatch                    | Wrong type, wrong arity, non-bool condition, break/continue outside loop, duplicate declaration |
| Z0403  | Missing return value             | Non-void function has a path with no `return` |
| Z0404  | Private member access violation  | `private` field or method accessed outside its class |
| Z0405  | Invalid modifier combination     | `abstract sealed`, modifier on enum member, etc. |
| Z0406  | Integer literal out of range     | Literal exceeds the declared explicit-size type's range (`i8 x = 200`) |

---

## Z05xx — IR Code Generator

| Code   | Title                                | When it occurs |
|--------|--------------------------------------|----------------|
| Z0501  | Unsupported syntax in code generation | A valid-syntax construct is not yet lowered to IR |

---

## Z09xx — Native Interop (FFI)

由 spec [`design-interop-interfaces`](../../spec/changes/design-interop-interfaces/) (C1) 占位；具体语义在 C2–C5 spec 中钉死。Z0901–Z0904 用于 L1 `[Native]` dispatch（已启用）；Z0905–Z0910 是 L2+ 三层 ABI 预留段。

### Z0901–Z0904（L1 `[Native]` 已启用）

| Code  | Title                            | When it occurs |
|-------|----------------------------------|----------------|
| Z0901 | UnknownNativeName                | `[Native("__name")]` 中的 `__name` 不在 VM `dispatch_table` 内 |
| Z0902 | NativeArityMismatch              | `extern` 方法参数数量与 `dispatch_table` 注册项不一致 |
| Z0903 | ExternMissingNativeAttribute     | `extern` 方法缺少 `[Native]` 标注 |
| Z0904 | NativeAttributeOnNonExtern       | `[Native]` 标注用在非 `extern` 方法上 |

### Z0905 / Z0906 / Z0910（C2 已启用，2026-04-29）

由 `src/runtime/src/native/{registry,error,loader,exports}.rs` 在 `z42_register_type` / `z42_resolve_type` / `VmContext::load_native_library` / `Instruction::CallNative` 路径上抛出。错误信息通过 thread-local `LAST_ERROR` 透传给 `z42_last_error()`，同时以 `anyhow::Error` 形式向 z42 解释器返回。

| Code  | Title                            | When it occurs |
|-------|----------------------------------|----------------|
| Z0905 | NativeTypeRegistrationFailure    | `z42_register_type` 收到 `null` descriptor / `null` `module_name` 或 `type_name` / `method_count > 0` 但 `methods == NULL` / 方法签名解析失败 / `(module, type)` 在 VM 内已存在；`CallNative` 找不到 `(module, type)` 或 `symbol`；`z42_invoke` / `z42_invoke_method` 在 C2 阶段尚未实现的 reverse-call 路径 |
| Z0906 | AbiVersionMismatch               | `z42_register_type` 接收的 `Z42TypeDescriptor_v1.abi_version` 与 VM 期望的 `Z42_ABI_VERSION` 不一致 |
| Z0910 | NativeLibraryLoadFailure         | `VmContext::load_native_library` 中 `libloading::Library::new(path)` 失败（路径不存在 / 架构不匹配 / 权限错误）或目标库缺少 `<basename>_register` 入口符号 |

### Z0908（C4 runtime + C5 syntax + C8 marshal 已启用，2026-04-29）

由 VM runtime（`src/runtime/src/interp/exec_instr.rs` + `src/runtime/src/native/marshal.rs`）和 TypeChecker（`src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs`）联合抛出。

| Code   | Title                            | When it occurs |
|--------|----------------------------------|----------------|
| Z0908  | NativeMarshalConstraintViolation (runtime) | (a) `PinPtr` 收到非 `Value::Str` / `Value::Array` source；(b) `UnpinPtr` 收到非 `Value::PinnedView`（IR 损坏或 compiler emit 不配对）；(c) `FieldGet` on `PinnedView` 访问 `ptr` / `len` 之外的字段名；(d) **(spec C8)** `marshal::value_to_z42` 把含 interior NUL 的 `Value::Str` 投到 `*const c_char`；(e) **(spec C10)** `PinPtr` Array 源含**非 `Value::I64` 元素**或元素**超出 `0..=255`**（仅每元素都是 u8 范围 i64 时可作为字节缓冲 pin） |
| E0908a | PinnedNotPinnable (TypeCheck, spec C5) | `pinned p = <expr> { ... }` 中 `<expr>` 类型不是 `string` |
| E0908b | PinnedControlFlow (TypeCheck, spec C5) | `pinned` 块体内含 `return` / `break` / `continue` / `throw` —— C5 范围内严格禁止；放开需要 IR 层 try-finally-like UnpinPtr emission，留给后续 spec |

### E0907（C6 已启用，2026-04-29）

| Code  | Title                            | When it occurs |
|-------|----------------------------------|----------------|
| E0907 | NativeAttributeMalformed (parser/typecheck, specs C6 + C9) | (a) parser：`[Native(...)]` 含未知键 / 值不是 string literal / 完全无键；(b) typecheck (spec C9)：method-level 与 class-level 拼接后的 Tier1 binding 仍缺 lib/type/entry 任一字段。原"manifest-vs-declaration 签名校验"占位含义由后续 source generator spec 接管，复用编号 |

### E0909（C11a 已启用，2026-04-30）

由 `Z42.Project.NativeManifest.Read` 在加载 `.z42abi` manifest 时抛出 `NativeManifestException`。错误码以 `E` 前缀形式归入编译器/工具链域。

| Code  | Title              | When it occurs |
|-------|--------------------|----------------|
| E0909 | ManifestParseError | (a) manifest 文件不存在 / IO 失败；(b) JSON 不合法；(c) `abi_version` 不等于 `NativeManifest.ExpectedAbiVersion`（当前 = 1）；(d) 缺必需字段（`module` / `library_name` / `types`） |

### E0916（C11b 已启用，2026-04-30；C11e 扩展，2026-05-06）

由 `Z42.Semantics.Synthesis.NativeImportSynthesizer` 在合成 ClassDecl 时抛出 `NativeImportException`，由 `PackageCompiler` 在 source-loop 中捕获并写入诊断输出。

| Code  | Title                          | When it occurs |
|-------|--------------------------------|----------------|
| E0916 | NativeImportSynthesisFailure   | (a) `import T from "lib";` 中 `T` 不在 manifest 的 `types[]` 中；(b1, **unsupported-shape**) manifest 某 method 的 `params` / `ret` 用了 C11e 白名单外的类型形态（白名单：primitives / `Self` / `*mut/const Self` / `*const c_char` (param-only) / `*mut/const <Imported>`）——错误信息包含当前已 import 的 native type 列表；(b2, **unknown-type**) `*mut/const <X>` 中 X 不是 `c_char` / `Self`，且未在当前 CompilationUnit 中 `import`——错误信息含 ``import X from "...";`` 提示；(b3) `*const c_char` / `*mut c_char` 出现在 ret 位置——错误信息含 "c_char return"、"C11f"（ownership 协议未定，留 C11f）；(c) 同名 type 被两条 import 声明但 lib 不同；(d) `kind=="method"` 的 entry 第一参数不是 `*mut/const Self`；(e) `DefaultNativeManifestLocator` 在 `<sourceDir>` 与 `Z42_NATIVE_LIBS_PATH` 中均找不到 `<lib>.z42abi` |

### E0911 / E0912 / E0914 / E0915（R4.A 已启用，2026-04-30）

由 spec [`compiler-validate-test-attributes`](../../spec/changes/compiler-validate-test-attributes/) (R4) 钉死；R1.C parser 收集 `[Test]` / `[Benchmark]` / `[Skip]` / `[Setup]` / `[Teardown]` / `[Ignore]` 6 个 attribute 后，本 pass 在 TypeCheck 之后、IrGen 之前校验签名 + 组合合法性。实施位置：[`src/compiler/z42.Semantics/TestAttributeValidator.cs`](../../src/compiler/z42.Semantics/TestAttributeValidator.cs)。

| Code   | Title                            | When it occurs |
|--------|----------------------------------|----------------|
| E0911  | TestSignatureInvalid             | `[Test]` 函数签名错误：必须 `fn() -> void`、不能泛型；`[Test]` 与 `[Benchmark]` 互斥 |
| E0912  | BenchmarkSignatureInvalid        | `[Benchmark]` 部分签名校验：返回 void、不能泛型。**完整** "首参为 Bencher" 校验等 R2.C 提供 Bencher 类型后启用 |
| E0913  | ShouldThrowTypeInvalid           | （**预留**，R4.B）`[ShouldThrow<E>]` 中 E 不存在 / 非 Exception 子类型 / 未搭配 `[Test]`；当前 parser 不支持泛型 attribute 语法，故未被触发 |
| E0914  | SkipReasonMissing                | `[Skip]` 缺 `reason` 参数（或 reason 为空字符串）；或 `[Skip]` / `[Ignore]` 单独使用（必须搭配 `[Test]` / `[Benchmark]`） |
| E0915  | SetupTeardownSignatureInvalid    | `[Setup]` / `[Teardown]` 签名错误（需 `fn() -> void`）；或与 `[Test]` / `[Benchmark]` / `[Skip]` / `[Ignore]` 同函数标注（互斥） |

---

## E06xx — Package / Import Resolution（strict-using-resolution，2026-04-28）

由 TypeChecker 在导入符号过滤后报出（参见 [namespace-using.md](namespace-using.md#strict-using-resolution-2026-04-28)）。

| Code   | Title                                | When it occurs |
|--------|--------------------------------------|----------------|
| E0601  | Type name collision across packages  | 两个被激活的包在同一 namespace 下声明同名 class（first-wins 已禁用） |
| E0602  | Unresolved `using` namespace         | `using <ns>;` 声明的 namespace 没有任何已加载包提供 |
| W0603  | Package declares reserved namespace  | 非 stdlib 包（不以 `z42.` 开头）声明 `Std` / `Std.*` 命名空间（warn-only） |

---

## WSxxx — Workspace Manifest（C1，2026-04-26）

由 `Z42Errors` 工厂方法（`src/compiler/z42.Project/ManifestErrors.cs`）抛出，message 中含 `error[WSxxx]:` 或 `warning[WSxxx]:` 前缀。

### C1 已启用

| Code  | Title                            | When it occurs |
|-------|----------------------------------|----------------|
| WS003 | ForbiddenSectionInMember         | Member `<name>.z42.toml` 含 `[workspace.*]` / `[policy]` / `[profile.*]` 段 |
| WS005 | AmbiguousManifest                | 同一 member 目录有两份 `*.z42.toml` |
| WS007 | OrphanMember (warning)           | Manifest 在 workspace 子树内但未被 `members` 命中（不阻塞构建） |
| WS030 | InvalidWorkspaceFileName         | `[workspace]` 段出现在非 `z42.workspace.toml` 文件 |
| WS031 | InvalidDefaultMembers            | `default-members` 引用了未匹配的成员 |
| WS032 | WorkspaceFieldNotFound           | Member 写 `xxx.workspace = true` 但根 `[workspace.project]` 未声明该字段 |
| WS033 | InvalidWorkspaceProjectField     | `[workspace.project]` 字段类型错误 / 不可共享字段（如 `name`）被声明 |
| WS034 | WorkspaceDependencyNotFound      | Member 引用未在 `[workspace.dependencies]` 中声明的依赖 |
| WS035 | LegacyWorkspaceVersionSyntax     | 出现已废弃的 `version = "workspace"` 语法 |
| WS036 | RootManifestMustBeVirtual        | `z42.workspace.toml` 同时含 `[workspace]` 与 `[project]` |
| WS037 | UnknownTemplateVariable          | 路径模板含未知变量（含 `${env:NAME}` 暂不支持） |
| WS038 | InvalidTemplateSyntax            | 模板嵌套 / 未闭合 / 非法字符 |
| WS039 | TemplateVariableNotAllowed       | 模板变量出现在不允许的字段（如 `version`） |

### C2 已启用（2026-04-26）

| Code  | Title                            | When it occurs |
|-------|----------------------------------|----------------|
| WS020 | CircularInclude                  | Include 链含直接或间接循环（A→A 或 A→B→A） |
| WS021 | ForbiddenSectionInPreset         | Preset 文件含 `[workspace.*]` / `[policy]` / `[profile.*]` / `[project].name` / `[project].entry` |
| WS022 | IncludeTooDeep                   | Include 嵌套深度超过 8 层 |
| WS023 | IncludePathNotFound              | Include 指向的文件不存在 |
| WS024 | IncludePathNotAllowed            | Include 路径含绝对系统路径 / URL / glob 模式 |

### C3 已启用（2026-04-26）

| Code  | Title                            | When it occurs |
|-------|----------------------------------|----------------|
| WS010 | PolicyViolation                  | Member 显式声明的字段值与 workspace `[policy]` 锁定值冲突 |
| WS011 | PolicyFieldPathNotFound          | `[policy]` 段含未知字段路径（附 fuzzy 建议） |

### C4a 已启用（2026-04-26）

| Code  | Title                            | When it occurs |
|-------|----------------------------------|----------------|
| WS001 | DuplicateMemberName              | 两个 members 声明同一 `[project] name` |
| WS002 | ExcludedMemberSelected           | `-p` 与 `--exclude` 同时指定同一 member |
| WS006 | CircularDependency               | Member 间依赖图含环（DFS 三色检测） |

### C4b/C4c 状态

C4b 不新增 WSxxx；C4c 已移除 WS004（归并入 WS010）。

---

## Adding a new code

1. Add a `public const string Xxx = "Z0nnn";` to `DiagnosticCodes` in [Diagnostic.cs](../../src/compiler/z42.Compiler/Diagnostics/Diagnostic.cs).
2. Add an entry to `DiagnosticCatalog.All` in [DiagnosticCatalog.cs](../../src/compiler/z42.Compiler/Diagnostics/DiagnosticCatalog.cs) with title, description, and optional example.
3. Add a row to the relevant table in this file.
