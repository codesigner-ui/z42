# Tasks: Native InternalCall

> 状态：🟢 已完成 | 创建：2026-04-05 | 完成：2026-04-05

## 进度概览

- [x] 阶段 1: Lexer + Parser
- [x] 阶段 2: NativeTable + TypeChecker
- [x] 阶段 3: Codegen
- [x] 阶段 4: VM HashMap dispatch 重构
- [x] 阶段 5: VM stdlib 自动加载
- [x] 阶段 6: stdlib 源文件更新 + build 脚本
- [x] 阶段 7: 测试与验证
- [x] 阶段 8: 文档同步

---

## 阶段 1: Lexer + Parser

- [x] 1.1 `TokenDefs.cs` Keywords 字典加 `{ "extern", TokenKind.Extern }`
- [x] 1.2 `TokenKind.cs` 枚举加 `Extern`（放在 `Abstract` 附近）
- [x] 1.3 `TopLevelParser.cs` — 新增 `TryParseNativeAttribute(ref cursor) → string?`
- [x] 1.4 顶层函数循环：`pendingNative = TryParseNativeAttribute`；传给 `ParseFunctionDecl`
- [x] 1.5 类 body 循环：同 1.4
- [x] 1.6 `ParseNonVisibilityModifiers` 返回元组增加第 6 项 `isExtern: bool`
- [x] 1.7 `ParseFunctionDecl` 增加 `string? nativeIntrinsic` 参数；extern 方法期望 `;`
- [x] 1.8 `Ast.cs` `FunctionDecl` 增加 `bool IsExtern, string? NativeIntrinsic`
- [x] 1.9 单元测试（LexerTests.cs + ParserTests.cs）

---

## 阶段 2: NativeTable + TypeChecker

- [x] 2.1 新建 `src/compiler/z42.IR/NativeTable.cs`（~80 个 intrinsic）
- [x] 2.2 `DiagnosticCatalog.cs` 新增 Z0901/Z0902/Z0903/Z0904
- [x] 2.3 `TypeChecker.cs` — extern 检查：Z0901/Z0902/Z0903/Z0904
- [x] 2.4 extern 方法跳过函数体 typecheck
- [x] 2.5 单元测试（TypeCheckerTests.cs）

---

## 阶段 3: Codegen

- [x] 3.1 `IrGen.cs` — `EmitNativeStub`；extern 方法直接 return
- [x] 3.2 IrGenTests.cs — 4 个 extern codegen 测试

---

## 阶段 4: VM HashMap dispatch 重构

- [x] 4.1 `builtins.rs` — 所有 builtin 抽为独立命名函数
- [x] 4.2 `Value` 借用方法（`as_str` 等已在 builtins.rs 内使用）
- [x] 4.3 `OnceLock<HashMap<&'static str, NativeFn>>` 全局静态 DISPATCH
- [x] 4.4 `exec_builtin` 改为 HashMap dispatch；签名不变
- [x] 4.5 `vm.rs` / `Vm::new` 不修改
- [x] 4.6 builtins mod 内单元测试

---

## 阶段 5: VM stdlib 自动加载

- [x] 5.1 `main.rs` 启动序列：加载 z42.core.zpkg + 依赖 + 用户 artifact → merge
- [x] 5.2 `loader.rs` `LoadedArtifact` 增加 `dependencies: Vec<ZpkgDep>`
- [x] 5.3 `metadata/mod.rs` 重新导出 `merge_modules`

---

## 阶段 6: stdlib 源文件更新 + build 脚本

- [x] 6.1 所有 `[Native("__xxx")]` 方法加 `extern` 关键字
- [x] 6.2 `TopLevelParser.cs` 新增表达式体方法支持（`=> expr;`）
- [x] 6.3 简化 stdlib 文件（移除 Phase 1 不支持的类型转换 overloads）
- [x] 6.4 新建 `scripts/build-stdlib.sh`（编译到 `artifacts/z42/libs/`）
- [x] 6.5 验证 build-stdlib.sh 成功：5/5 libs，各 zpkg > 0 bytes

---

## 阶段 7: 测试与验证

- [x] 7.1 `dotnet build` — 0 error
- [x] 7.2 `cargo build` — 0 error
- [x] 7.3 `dotnet test` — 381/381 passed
- [x] 7.4 `./scripts/test-vm.sh` — 84/84 passed (42 interp + 42 jit)
- [x] 7.5 `./scripts/build-stdlib.sh` — 5 succeeded, 0 failed

---

## 阶段 8: 文档同步

- [x] 8.1 `docs/design/language-overview.md` — §15 extern + [Native] + 表达式体
- [x] 8.2 `CLAUDE.md` — build-stdlib.sh 命令
- [x] 8.3 `docs/roadmap.md` — extern/InternalCall + 表达式体进度表；M7 → 🚧

---

## 备注

- 表达式体方法 `=> expr;` 作为附带改动实现（stdlib 需要）
- stdlib 的类型转换重载（依赖 cast 语法和方法重载解析）暂缓到 L2
- `build` 命令输出到 `src/libraries/<lib>/dist/`，build-stdlib.sh 从 dist/ 拷贝到 artifacts/
