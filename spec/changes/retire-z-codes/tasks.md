# Tasks: Retire Z#### Codes

> 状态：🟡 进行中 | 创建：2026-05-11
> 类型：vm（VM throw 语义 + stdlib type 添加）
> 关联：[proposal.md](proposal.md) + [design.md](design.md) + [specs/diagnostics/spec.md](specs/diagnostics/spec.md)

## 进度概览

- [ ] 阶段 1: 添加 `Std.InvalidMarshalException` stdlib 类型 + 测试
- [ ] 阶段 2: VM throw site 改造（Z0908 → stdlib exception；Z0905/Z0906/Z0910 去前缀）
- [ ] 阶段 3: 删除 Rust catalog 基础设施
- [ ] 阶段 4: 删除 C# RustErrorCatalog + csproj embed
- [ ] 阶段 5: 删除 z42-vm `--explain` / `--list-errors` flag
- [ ] 阶段 6: 删除 `docs/error-codes/` 整个目录
- [ ] 阶段 7: 测试更新 + GREEN 验证
- [ ] 阶段 8: 归档加注 + 文档同步 + commit + push

每阶段独立 commit + `dotnet test` + `./scripts/test-vm.sh` 全绿才进下一阶段。

---

## 阶段 1: 添加 `Std.InvalidMarshalException`

- [ ] 1.1 NEW [src/libraries/z42.core/src/Exceptions/InvalidMarshalException.z42](../../../src/libraries/z42.core/src/Exceptions/InvalidMarshalException.z42)（或 stdlib 现行组织位置）:
  - `namespace Std;`
  - `public class InvalidMarshalException : Exception { public InvalidMarshalException(string message) : base(message) { } }`
  - 含完整 docstring（design.md 提供的版本）
- [ ] 1.2 验证 stdlib build：`./scripts/build-stdlib.sh` 或等价命令（参考 CLAUDE.md 现行 stdlib build 路径）
- [ ] 1.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿（含 stdlib symbol 收集）
- [ ] 1.4 commit: `feat(stdlib): retire-z-codes S1 — add Std.InvalidMarshalException`

## 阶段 2: VM throw site 改造

### 2.1 Z0908 → stdlib exception

- [ ] 2.1.1 确认 / 扩展 `src/runtime/src/exception/` 提供 `throw_stdlib_exception(type_fq, message)` API（如不存在，加薄 helper；参考 commit `3d4c790 exception-stack-trace`）
- [ ] 2.1.2 MODIFY [src/runtime/src/native/marshal.rs](../../../src/runtime/src/native/marshal.rs):
  - `anyhow!("Z0908: ...")` 路径改为 throw `Std.InvalidMarshalException` 实例
  - Fallback：若 stdlib 不可用，降级 anyhow! 带类型提示
- [ ] 2.1.3 MODIFY [src/runtime/src/native/marshal_tests.rs](../../../src/runtime/src/native/marshal_tests.rs):
  - 删除 `assert!(msg.contains("Z0908"))`
  - 改为断言 z42 异常类型 + Message 内容
- [ ] 2.1.4 新增端到端测试 `src/tests/parse/marshal_nul_throws/`:
  - source.z42：z42 user code 触发 marshal NUL，catch `Std.InvalidMarshalException`
  - expected_output.txt：断言异常被正确 catch + Message 含 expected 子串 + StackTrace 存在
- [ ] 2.1.5 `cargo test -p z42_vm` 全绿
- [ ] 2.1.6 `./scripts/test-vm.sh` 全绿

### 2.2 Z0905 / Z0906 / Z0910 去前缀

- [ ] 2.2.1 MODIFY [src/runtime/src/native/registry.rs](../../../src/runtime/src/native/registry.rs):
  - 所有 `anyhow!("Z0905: ...")` 改为 `anyhow!("...")`（去前缀，保留诊断文本）
  - grep 验证：`grep -n "Z0905" src/runtime/src/native/registry.rs` 应零结果
- [ ] 2.2.2 MODIFY [src/runtime/src/native/loader.rs](../../../src/runtime/src/native/loader.rs):
  - 替换 `Z0910` 引用为字符串字面量；去 anyhow message 中 `Z0910:` 前缀
- [ ] 2.2.3 MODIFY / DELETE [src/runtime/src/native/error.rs](../../../src/runtime/src/native/error.rs):
  - 删除 `pub const Z0910: u32 = 910;` 常量
  - 检查其他 Z 常量（Z0906 等）—— 删除所有
- [ ] 2.2.4 grep 全仓 `Z[0-9]{4}` 在 *.rs 中应零匹配（doc comments 可保留历史引用，但代码无）
- [ ] 2.2.5 `cargo test -p z42_vm` 全绿
- [ ] 2.2.6 commit: `refactor(vm): retire-z-codes S2 — drop Z#### prefix from VM throw sites`

## 阶段 3: 删除 Rust catalog 基础设施

- [ ] 3.1 DELETE [src/runtime/src/diagnostics/mod.rs](../../../src/runtime/src/diagnostics/mod.rs)
- [ ] 3.2 DELETE [src/runtime/src/diagnostics/tests.rs](../../../src/runtime/src/diagnostics/tests.rs)
- [ ] 3.3 rmdir `src/runtime/src/diagnostics/`（空目录）
- [ ] 3.4 MODIFY [src/runtime/src/lib.rs](../../../src/runtime/src/lib.rs):
  - 删除 `pub mod diagnostics;`
- [ ] 3.5 DELETE `src/runtime/src/native/registry_audit_tests.rs`（drift-guard test）
  - 检查是否在 main.rs / lib.rs 有 `#[cfg(test)] mod registry_audit_tests;` 引用，一起删
- [ ] 3.6 `cargo build -p z42_vm` 通过
- [ ] 3.7 commit: `refactor(vm): retire-z-codes S3 — delete diagnostics catalog module + drift-guard test`

## 阶段 4: 删除 C# RustErrorCatalog + csproj embed

- [ ] 4.1 DELETE [src/compiler/z42.Core/Diagnostics/RustErrorCatalog.cs](../../../src/compiler/z42.Core/Diagnostics/RustErrorCatalog.cs)
- [ ] 4.2 MODIFY [src/compiler/z42.Core/z42.Core.csproj](../../../src/compiler/z42.Core/z42.Core.csproj):
  - 删除 `<EmbeddedResource Include="..\..\..\docs\error-codes\Z.json" LogicalName="Z42.Core.Diagnostics.Z.json" />` 整段（含包裹的 `<ItemGroup>` 若仅含该条）
- [ ] 4.3 检查 `DiagnosticCatalog.RegisterExternal` 是否有 caller 仍引用 `RustErrorCatalog`，清理
- [ ] 4.4 检查测试 `src/compiler/z42.Tests/DiagnosticCatalogRoutingTests.cs` 等是否引用 `RustErrorCatalog`，更新或删除相关 case
- [ ] 4.5 `dotnet build src/compiler/z42.slnx` 通过
- [ ] 4.6 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [ ] 4.7 commit: `refactor(compiler): retire-z-codes S4 — delete RustErrorCatalog + Z.json embed`

## 阶段 5: 删除 z42-vm `--explain` / `--list-errors`

- [ ] 5.1 MODIFY [src/runtime/src/main.rs](../../../src/runtime/src/main.rs):
  - 删除 `explain: Option<String>` 字段
  - 删除 `list_errors: bool` 字段
  - 删除 `--explain` / `--list-errors` 分支处理代码
  - `file` argument 的 `#[arg(required_unless_present_any = ["explain", "list_errors"])]` 改为标准 `required`
- [ ] 5.2 `cargo build --release -p z42_vm` 通过
- [ ] 5.3 手动验证：`./artifacts/rust/release/z42-vm --help` 不含 `--explain` / `--list-errors`
- [ ] 5.4 检查测试 `src/runtime/tests/` 是否有 explain 相关，更新或删除
- [ ] 5.5 z42c explain 路径检查：
  - `dotnet run -p src/compiler/z42.Driver -- explain E0401` 正常输出
  - `dotnet run -p src/compiler/z42.Driver -- explain Z0905` 输出 friendly hint + 非零退出码
- [ ] 5.6 MODIFY [src/compiler/z42.Driver/Program.cs](../../../src/compiler/z42.Driver/Program.cs):
  - 若有引用 RustErrorCatalog 的代码，清理
  - explain 命令 handler 对未知 code 输出 hint：`"Unknown error code '{code}'. Note: Z#### runtime codes were retired (2026-05-11); use exception types instead."`
- [ ] 5.7 commit: `refactor(cli): retire-z-codes S5 — drop --explain / --list-errors from z42-vm`

## 阶段 6: 删除 `docs/error-codes/`

- [ ] 6.1 DELETE [docs/error-codes/Z.json](../../../docs/error-codes/Z.json)
- [ ] 6.2 DELETE [docs/error-codes/README.md](../../../docs/error-codes/README.md)
- [ ] 6.3 rmdir `docs/error-codes/`（空目录）
- [ ] 6.4 grep 全仓 `docs/error-codes` 引用 → 修复（注释 / 文档链接）
- [ ] 6.5 commit: `refactor(docs): retire-z-codes S6 — delete docs/error-codes/ entirely`

## 阶段 7: 测试更新 + GREEN 验证

- [ ] 7.1 完整运行：
  - `dotnet build src/compiler/z42.slnx` —— 无编译错误
  - `cargo build -p z42_vm` —— 无编译错误
  - `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` —— 全绿（含 stdlib + diagnostic 测试）
  - `./scripts/test-vm.sh` —— 全绿（含新增 marshal-nul-throws 测试）
- [ ] 7.2 grep 验证彻底无 Z 残留：
  - `grep -rn 'Z[0-9]\{4\}' src/runtime/src/ --include='*.rs'` = 空（除可能 doc comments 中历史引用）
  - `grep -rn 'Z[0-9]\{4\}' src/compiler/ --include='*.cs'` = 空
  - `grep -rn 'docs/error-codes' src/ docs/ --include='*.cs' --include='*.rs' --include='*.md'` = 空
- [ ] 7.3 手动 smoke test：
  - `z42-vm hello.zbc` 正常运行
  - `z42-vm --help` 不含 explain flags
  - `z42c explain E0401` 输出正确
  - `z42c explain Z0905` 输出 hint + exit 非 0

## 阶段 8: 归档加注 + 文档同步 + 归档本 spec

- [ ] 8.1 在 `spec/archive/<日期>-z-catalog-and-cross-language-sync/` 顶部（README.md 或 tasks.md）加 banner:
  ```markdown
  ⚠️ 2026-05-11 NOTE: 本 spec 引入的 Z#### catalog 共享基础设施已由
  `retire-z-codes` (spec/archive/2026-05-11-retire-z-codes/) 整体反向；
  本归档保留作为历史决策追溯，不删除。
  ```
- [ ] 8.2 MODIFY [docs/review.md](../../../docs/review.md) 修订记录追加：
  - "2026-05-11: retire-z-codes 落地 — Z#### codes 整体退役；docs/error-codes/ 删除；exceptions 走 stdlib 类型化 + stack trace 自描述路径；z-catalog-and-cross-language-sync 反向"
- [ ] 8.3 (可选) MODIFY `docs/design/runtime/diagnostics.md` 或对应设计文档，记录"E#### 编译期 + 类型化运行期异常"的分层
- [ ] 8.4 tasks.md 状态改 🟢 已完成
- [ ] 8.5 移动 `spec/changes/retire-z-codes/` → `spec/archive/2026-05-11-retire-z-codes/`
- [ ] 8.6 commit: `docs+spec(diagnostics): retire-z-codes — archive + cross-spec annotation`
- [ ] 8.7 push origin main

## 备注

### 不解决的问题（follow-up spec 处理）
- 其他 stdlib 异常类型增补（Std.InvalidCastException 等）—— 独立 spec
- E#### catalog JSON 化 / 跨语言（compiler / IDE）共享 —— 独立 spec（如果有需要）
- z42c explain 输出格式增强（JSON / 颜色 / 链接 docs）—— 独立 spec

### 风险监控
- **Phase 1 验证点**：`dotnet test` 含 stdlib 加载 → 证明新类型可被 TypeChecker 发现 + 实例化
- **Phase 2 验证点**：`./scripts/test-vm.sh` 含 marshal-nul-throws → 证明 throw + catch + stack trace 端到端
- **Phase 7 验证点**：grep 残留检查 → 证明 Z 字符串/引用 / docs/error-codes/ 引用全部清理
