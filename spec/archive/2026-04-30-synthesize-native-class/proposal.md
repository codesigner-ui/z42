# Proposal: Native class synthesis from manifest (C11b — Path B1)

## Why

C11a 已经能：(1) 解析 `import T from "lib";` 顶层语法把 `NativeTypeImport` 收集到 `cu.NativeImports`；(2) `NativeManifest.Read(path)` 把 `.z42abi` 读成 `ManifestData`。两端打通了**数据通路**，但**编译器尚不消费**——`NativeImports` 列表写完没人读，TypeChecker 看不到 native 类型。

C11b 把这条数据通路接通：在 Parser 与 TypeChecker 之间插一个 **`NativeImportSynthesizer` 编译期 pass**，把每个 `import T from "lib";` 翻译成一个**合成的 `ClassDecl`** 注入到 `cu.Classes`。从 TypeChecker 起，合成类与用户手写类**完全一视同仁**：成员解析、类型推导、IR codegen、CallNative dispatch 全部走既有路径。

这之后用户写：

```z42
import Counter from "numz42";

void Main() {
    var c = new Counter();
    c.inc();             // → CallNative numz42_Counter_inc(handle)
    int v = c.get();
}
```

**手写零行**绑定，TypeChecker 全程类型安全，IDE 自动补全可见——这是从 C2 起一路推动 C11 的根本目的。

## Scope this spec — Path B1（handle 风味）

经讨论锁定 **B1 路径**（详见 design.md §"Path B 内部权衡"）：
- 合成的类**不暴露脚本字段**——所有数据藏在 native 端的 opaque handle 后面
- VM 端 **零改动**——C2–C10 既有的 Z42TypeDescriptor + libffi dispatch 原封套用
- 用户视角的"字段"必须通过 manifest 中的 `method` 暴露（如 `count` 字段对应 `get_count() / set_count(v)`）
- C11b 是**纯编译期 pass** + **manifest 签名解析器**

不做（保留给后续 spec）：
- **C11c**：Path B2 ——脚本可见字段 + VM 端 `z42_obj_*` 字段访问 ABI
- **C11d**：脚本端 `class` 上 `[Repr(C)]` attribute 让脚本声明布局对齐 native struct（用户提议的"内存布局后面在脚本映射"）
- 自动 dlopen / 注册时机（仍由 test harness 预注册 numz42-c）
- 编译期"用户调用签名 vs manifest 签名"双向校验（manifest 暂作单向真相）

## What Changes

- **新 pass `NativeImportSynthesizer`**（`src/compiler/z42.Semantics/Synthesis/`）
  - 输入：`CompilationUnit` + `INativeManifestLocator`（注入式，便于测试）
  - 输出：原 `cu.Classes` 末尾追加合成的 `ClassDecl` 列表
  - 失败抛 `NativeManifestException` (E0909) 或 `NativeImportException` (E0916)
- **新错误码 E0916 `NativeImportSynthesisFailure`**
  - manifest 不含被 import 的 type
  - manifest 签名串无法解析（C11b 只支持白名单内类型，不支持的报 E0916）
  - 同 type 出现在多条 `import` 且 lib 不一致
- **manifest 签名解析器**（`ManifestSignatureParser`）
  - 支持：primitives (`i8` / `i16` / `i32` / `i64` / `u8` / ... / `f32` / `f64` / `bool` / `void`)、`Self` (return)、`*mut Self` / `*const Self` (receiver)
  - 不支持：`*const c_char` / 用户类型 / Array / Option / 嵌套泛型 → 报 E0916（待后续 spec 扩展）
- **manifest 路径解析**：`<dir(source)>/<lib>.z42abi` 默认；`Z42_NATIVE_LIBS_PATH`（colon-separated）次选；测试用注入式 `INativeManifestLocator`
- **Pipeline 接通**：`SemanticAnalyzer` / `Compiler` 的入口先跑 `NativeImportSynthesizer`，再交给 `TypeChecker`
- **测试**：签名解析器单测 + 合成器端到端 + 错误路径

## Scope（允许改动的文件）

| 文件 | 变更 |
|------|------|
| `src/compiler/z42.Semantics/Synthesis/NativeImportSynthesizer.cs` | NEW 合成器主体 |
| `src/compiler/z42.Semantics/Synthesis/ManifestSignatureParser.cs` | NEW manifest 签名串 → TypeExpr |
| `src/compiler/z42.Semantics/Synthesis/INativeManifestLocator.cs` | NEW 路径解析接口 + 默认实现 |
| `src/compiler/z42.Semantics/Synthesis/NativeImportException.cs` | NEW 失败时抛出（含 Code = E0916） |
| `src/compiler/z42.Semantics/SemanticAnalyzer.cs` | MODIFY（如存在）—— 在 TypeChecker 前插 Synthesizer pass；不存在则在 Pipeline 入口处接 |
| `src/compiler/z42.Pipeline/PackageCompiler.cs` | MODIFY 接 Synthesizer 进 pipeline |
| `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` | MODIFY +`NativeImportSynthesisFailure = "E0916"` |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | MODIFY +E0916 entry |
| `src/compiler/z42.Tests/ManifestSignatureParserTests.cs` | NEW 签名解析单测 |
| `src/compiler/z42.Tests/NativeImportSynthesizerTests.cs` | NEW 端到端合成 + 错误路径 |
| `docs/design/error-codes.md` | MODIFY +E0916 |
| `docs/design/interop.md` | MODIFY +C11b roadmap 行 + B1/B2/C 路径表 |
| `docs/roadmap.md` | MODIFY +C11b 行 |

**只读引用**：
- `src/compiler/z42.Project/NativeManifest.cs` — 调用 `Read`
- `src/compiler/z42.Syntax/Parser/Ast.cs` — 构造 ClassDecl/FunctionDecl/Tier1NativeBinding
- `src/compiler/z42.Pipeline/PackageCompiler.Helpers.cs` — 理解既有 pipeline 顺序
- `docs/design/manifest-schema.json` — 字段语义参考
- `spec/archive/2026-04-30-manifest-reader-import/` — C11a 完整背景

## Out of Scope（明确推后）

- **Path B2** 脚本可见字段：留给 C11c
- **`[Repr(C)]` 脚本端布局映射**（用户提议）：留给 C11d
- 用户类型 / Array / Option / 字符串 marshal 的自动签名映射：留给 C11e（按需扩展白名单）
- 编译期签名双向校验（manifest ↔ 用户手写 `[Native(...)]`）
- VM 启动时自动 dlopen `.dylib`：仍走 test harness 预注册路径
- JIT/AOT 直接 emit native opcodes（C13+）

## Open Questions

- [ ] **Q1（已决）**：B1 还是 B2？→ **B1**，VM 零改动
- [ ] **Q2（待决）**：合成的 ClassDecl 默认可见性？倾向 **internal**（同顶层默认），用户 `import` 视为引入到当前 namespace
- [ ] **Q3（待决）**：同名 type 在两个 import 不同 lib 中出现？倾向 **E0916 报错**（不允许 type-name 冲突，强制用户重命名或拆 namespace）
