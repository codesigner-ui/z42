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

### Z0905–Z0910（L2+ 预留，C1 占位）

| Code  | Title                            | Reserved for spec | When (planned) |
|-------|----------------------------------|-------------------|----------------|
| Z0905 | NativeTypeRegistrationFailure    | C2 (`impl-tier1-c-abi`) | `z42_register_type` 失败：descriptor 无效 / 名字冲突 |
| Z0906 | AbiVersionMismatch               | C2 (`impl-tier1-c-abi`) | `Z42TypeDescriptor.abi_version` 与 VM 期望版本不一致 |
| Z0907 | NativeMethodSignatureMismatch    | C3/C5             | manifest 声明的方法签名与用户 `extern class` 声明不一致 |
| Z0908 | PinnedBlockConstraintViolation   | C4 (`impl-pinned-block`) | `pinned` 块内试图修改 / 重分配被借出的 String/Array |
| Z0909 | ManifestParseError               | C5 (`impl-source-generator`) | `.z42abi` JSON 解析失败 / Schema 校验不过 |
| Z0910 | NativeLibraryLoadFailure         | C2/C5             | dlopen / LoadLibrary 失败：库不存在 / 不兼容架构 |

> **占位约定**：本 spec 不实现 Z0905–Z0910 的具体抛出点；C1 验证只确保编号已注册、不与现有 Z0xxx 冲突。

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
