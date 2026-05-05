# Proposal: Extend manifest signature whitelist (C11e)

## Why

C11b 落地后，`import T from "lib";` 已经能合成 ClassDecl，但 `ManifestSignatureParser` 白名单极窄——只接受 primitives + `Self` + `*mut/const Self`。这够跑 demo（numz42 的 Counter），但**无法包真实 C 库**：

- `printf` / `fopen` / `strerror` 等用 `*const c_char`，C11b 一律报 E0916
- Rust 库经常返回别的 native handle 类型，比如 `regex_t` 上的 `regex_match(pattern: *const Regex) -> Match`——C11b 不支持引用其他 import 的类
- 自然导致 C11b 的 spec 测试只有"自指 Counter"一种形态

C11e 把白名单从 demo 级扩到能包真实 opaque-handle 库（`sqlite3` / `regex_t` / `FILE*` 这类）。

## Scope this spec

**新增白名单条目**（按重要性排序）：

1. **`*const c_char` / `*mut c_char` → `string`**：C 字符串。z42 端写 `string`，IrGen 已经有 C8 marshal `(Value::Str, SigType::CStr)` arena 路径——白名单加一行就够，无新 IR / 无新 marshal。
2. **`*const <OtherT>` / `*mut <OtherT>` → `NamedType("<OtherT>")`**：其中 `<OtherT>` 是**当前 CompilationUnit 中其他 `import` 的 type 名**或 `Self`。这让 native 类之间可以互相引用作为方法签名。

**显式不做**（留 C11e 后续 / C11f）：

- `Array<T>` / `&[T]` 数组形式（manifest schema 用 Rust 风格，需要确定数组语义+长度对协议）
- `Option<T>` / nullable 指针
- 定长数组 `[T; N]`
- 字符串返回值的所有权语义（borrowed vs owned；C 端释放责任）—— C11e 里 c_char 仅在 **param 位置** 接受；return 位置 c_char 留下一 spec 决定 ownership 规则
- 用户类型不存在时的错误信息升级（保持 E0916）

## What Changes

- **`ManifestSignatureParser.cs`** — 增 c_char 分支 + 用户类型查找回调
- **新 API**：`ParseReturn` / `ParseParam` 接收 `IReadOnlySet<string>? knownNativeTypes`，里面是当前 cu 已 import 的 type 名集合（含 Self 隐含）
- **`NativeImportSynthesizer.cs`** — 调用前先扫一遍 `cu.NativeImports` 收齐 `knownNativeTypes` 集合；签名解析时传入
- E0916 错误信息明确区分 "type unknown" 与 "shape unsupported"
- 测试：c_char param + 用户类型 param + 用户类型 return + 错误路径

## Scope（允许改动的文件）

| 文件 | 变更 |
|------|------|
| `src/compiler/z42.Semantics/Synthesis/ManifestSignatureParser.cs` | MODIFY 加 c_char 分支 + 用户类型查找；API 加 knownNativeTypes 参数 |
| `src/compiler/z42.Semantics/Synthesis/NativeImportSynthesizer.cs` | MODIFY 收 knownNativeTypes 并下传 |
| `src/compiler/z42.Tests/ManifestSignatureParserTests.cs` | MODIFY +c_char + 用户类型用例 |
| `src/compiler/z42.Tests/NativeImportSynthesizerTests.cs` | MODIFY +e2e c_char 方法 + native 类引用其他 native 类 |
| `docs/design/error-codes.md` | MODIFY E0916 触发清单更新（区分 unknown-type vs unsupported-shape） |
| `docs/design/interop.md` | MODIFY §11.5 / Roadmap 加 C11e 行 |
| `docs/roadmap.md` | MODIFY +C11e 行 |

**只读引用**：
- `spec/archive/2026-04-30-synthesize-native-class/` — 理解 C11b 既有契约
- `src/runtime/src/native/marshal.rs` — 确认 C8 已支持 `(Value::Str, SigType::CStr)`

## Out of Scope（明确推后）

- Array / slice / Option / 定长数组 — C11f
- c_char 返回值的 ownership 协议 — C11f
- Path B2（VM-owned 字段） — C11c
- Path C（脚本端 `[Repr(C)]`） — C11d

## Open Questions

- [ ] **Q1（已决）**：c_char param-only？→ **是**，return 位置先报 E0916 with "c_char return needs ownership protocol; tracked in C11f"
- [ ] **Q2（待决）**：用户类型查找时大小写敏感？→ **是**（Ordinal），与 C11b 的 conflict-detect 一致
- [ ] **Q3（待决）**：`*mut Other` 与 `*const Other` 是否产生不同语义？→ **不**，C11b/e 都视为同一类型引用（mutability 是 native 内部约束，z42 端无 const 概念）
