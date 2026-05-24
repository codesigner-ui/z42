# Tasks: fix stdlib naming convention violations (B1-B4)

> 状态：🟢 已完成 | 完成：2026-05-24 | 创建：2026-05-24 | 类型：refactor
> Spec 类型：minimal mode

**变更说明**：rename-primitives-to-pascal-case 当时 carved out 的延后清理项。一次性扫掉 4 类 [naming-conventions.md](../../../design/language/naming-conventions.md) 违规 + 顺手删除 §7 的 `_SCREAMING_SNAKE` 私有静态例外条款（与 §4 `_camelCase` 私有字段规则冲突，规范内部冗余）。

**变更类型**：纯 refactor — 不改语义、不动 VM、无新接口。`.zbc` / `.zpkg` 不需 bump（仅 stdlib regen）。

**Scope**（允许改动的文件）：

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| **B1 — 泛型参数单字母（违规 §6）→ `T` + 语义名** | | |
| `src/libraries/z42.core/src/Collections/Dictionary.z42` | MODIFY | `<K, V>` → `<TKey, TValue>` |
| `src/libraries/z42.core/src/Collections/KeyValuePair.z42` | MODIFY | `<K, V>` → `<TKey, TValue>` |
| `src/libraries/z42.core/src/Delegates/MulticastFunc.z42` | MODIFY | `<T, R>` → `<TArg, TResult>`（2 处：MulticastFunc + MulticastFuncSubscription）|
| `src/libraries/z42.core/src/Exceptions/MulticastException.z42` | MODIFY | `<R>` → `<TResult>` |
| **B2 — 公开静态 SCREAMING_SNAKE（违规 §7）→ PascalCase** | | |
| `src/libraries/z42.io/src/Stdio.z42` | MODIFY | `MODE_NULL / MODE_INHERIT / MODE_PIPE / MODE_FILE` → `Null / Inherit / Pipe / File` + 自调用 4 处 |
| `src/libraries/z42.io/src/Process.z42` | MODIFY | 4 处 callsite 更新 `Stdio.MODE_FILE` → `Stdio.File` |
| `src/libraries/z42.io/src/ProcessOutputStream.z42` | MODIFY | `FD_STDOUT / FD_STDERR` → `Stdout / Stderr`（FD 前缀冗余）+ 自调用 3 处 |
| `src/libraries/z42.io/src/ProcessHandle.z42` | MODIFY | 2 处 callsite 更新 |
| `src/libraries/z42.math/src/Math.z42` | MODIFY | `PI` → `Pi`（§1 速查表样例已是 `Pi`）|
| `src/libraries/z42.math/tests/math_basics.z42` | MODIFY | 2 处 `Math.PI` 更新 |
| `src/libraries/z42.math/tests/math_constants/source.z42` | MODIFY | 1 处 `Math.PI` 更新（注释中的 `Math.PI` 保留作历史记录 ok）|
| `examples/oop.z42` | MODIFY | 2 处 `Math.PI` 更新 |
| `src/libraries/z42.core/src/Platform.z42` | MODIFY | `OSKind.IOS` → `OSKind.Ios`（§10 缩略词 3+ 字母 PascalCase）+ 2 处 callsite 更新 |
| **B3 — 私有静态 SCREAMING_SNAKE（违规 §4）→ `_camelCase`** | | |
| `src/libraries/z42.encoding/src/Base64.z42` | MODIFY | `ALPHA` → `_alpha` + 1 处自调用 |
| `src/libraries/z42.encoding/src/Hex.z42` | MODIFY | `ALPHA_LOWER / ALPHA_UPPER` → `_alphaLower / _alphaUpper` + 2 处自调用 |
| `src/libraries/z42.compression/src/Zip.z42` | MODIFY | `SIG_LOCAL_FILE_HEADER / SIG_CENTRAL_DIR_HEADER / SIG_EOCD / METHOD_STORE / METHOD_DEFLATE` → `_sigLocalFileHeader / _sigCentralDirHeader / _sigEocd / _methodStore / _methodDeflate` + 5 处自调用 |
| **B4 — 局部变量 SCREAMING_SNAKE（违规 §5）→ camelCase** | | |
| `src/libraries/z42.random/src/Random.z42` | MODIFY | `SIGN_BIT / WRAP / MANTISSA_MASK` → `signBit / wrap / mantissaMask` + 3 处自调用 |
| **规范同步** | | |
| `docs/design/language/naming-conventions.md` | MODIFY | 删除 §7 的"私有 SCREAMING_SNAKE 例外"段（与 §4 私有字段 `_camelCase` 规则冗余/冲突），同时把 §11 反模式表中允许 SCREAMING_SNAKE 的例外清理 |

**Out of Scope**：
- 不动 Primitives/*（rename-primitives 已落，无 SCREAMING_SNAKE）
- 不动 archive 中的历史 spec
- 不动注释中的 `Math.PI` 历史叙述
- 不动 stdlib regen 产物（`./scripts/regen-golden-tests.sh` 自动产）

**文档影响**：
- naming-conventions.md §7 例外删除（实际是规范内部冗余消除，不改对外行为）
- B2 涉及公开 API（Stdio / Math / OSKind / ProcessOutputStream）—— pre-1.0 直接改无 deprecation（philosophy.md）

## Tasks

- [x] 1.1 **B1 dictionary / kv-pair**：`Dictionary<K,V>` → `<TKey,TValue>` + body 内部所有 `K` / `V` 替换；`KeyValuePair<K,V>` → `<TKey,TValue>` + 同
- [x] 1.2 **B1 multicast-func**：`MulticastFunc<T,R>` + `MulticastFuncSubscription<T,R>` → `<TArg,TResult>` + body 内 `T` / `R` 替换
- [x] 1.3 **B1 multicast-exception**：`MulticastException<R>` → `<TResult>` + body 内 `R` 替换
- [x] 1.4 **B2 Stdio**：`MODE_*` → `Null/Inherit/Pipe/File` + 4 处自调用；callsite in Process.z42 (4 处) 更新
- [x] 1.5 **B2 ProcessOutputStream + ProcessHandle**：`FD_STDOUT/FD_STDERR` → `Stdout/Stderr` + 3 自调用 + 2 callsite
- [x] 1.6 **B2 Math**：`PI` → `Pi` + callsites in math tests + examples/oop.z42
- [x] 1.7 **B2 OSKind**：`IOS` → `Ios` + 2 自调用
- [x] 1.8 **B3 encoding**：Base64.ALPHA / Hex.ALPHA_LOWER / Hex.ALPHA_UPPER + 自调用
- [x] 1.9 **B3 compression**：Zip.SIG_* / METHOD_* + 5 自调用
- [x] 1.10 **B4 random**：3 个局部变量 + 3 自调用
- [x] 1.11 **naming-conventions.md**：删除 §7 SCREAMING_SNAKE 例外段；清理 §反模式表对应行
- [x] 1.12 `dotnet build src/compiler/z42.slnx` 全绿
- [x] 1.13 `./scripts/build-stdlib.sh` 20 member 全成功
- [x] 1.14 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj --no-build` 1291/1291 全绿
- [x] 1.15 `./scripts/regen-golden-tests.sh --no-stdlib` 跑通
- [x] 1.16 静态 grep 检查：`grep -rn "^\s*\(public\|private\) static [^;]*[A-Z][A-Z_]\+\s*=" src/libraries 2>/dev/null` 应为零（除 archive）
- [x] 1.17 commit + push（单 commit；含本 spec）
- [x] 1.18 mv → `docs/spec/archive/2026-05-24-fix-stdlib-naming-violations/`

## 备注

—

实施期发现：

- Math.PI 漏 declaration site：perl regex `\bMath\.PI\b` 只匹配 dotted 访问，不匹配 `public static double PI = ...` 声明本身。后补 Edit 修正。
- IsIOS 方法 rename → IsIos：原 Scope 没列出，发现 `OSKind.IOS → Ios` 时顺手把 Platform.IsIOS() 也 rename（§10 缩略词规则同样适用于方法名）。callsite 在 z42.io/tests/platform.z42。
- Stdio MODE_* 冲突方案：直接用 `Null/Inherit/Pipe/File` 会与同名工厂方法冲突，所以用 `ModeNull/ModeInherit/ModePipe/ModeFile` 保留 Mode 前缀。
- ProcessOutputStream FD_STDOUT/FD_STDERR → Stdout/Stderr：去掉 FD 前缀（实现细节）。
- naming-conv-2 (Deferred 段的 "私有 const 是否允许 SCREAMING_SNAKE") 一并删除。
- pre-existing test-stdlib 失败（YAML / IO.binary / FileStream）与本 spec 无关 —— baseline (pre-B1-B4) 也 11 fail；B1-B4 后 9 fail（实际改善 2 个）。新失败均来自他人 in-flight specs（z42.yaml, IO Stream Readers / Writers / BufferedStream）。
