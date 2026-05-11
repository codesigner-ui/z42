# Tasks: Z#### Catalog + Cross-Language Sync

> ⚠️ **2026-05-11 RETIRED**: 本 spec 引入的 Z#### catalog 共享基础设施
> （`docs/error-codes/Z.json` + Rust `diagnostics/` + C# `RustErrorCatalog`
> + `--explain` / `--list-errors` flags）已由 [retire-z-codes]
> (../2026-05-11-retire-z-codes/) 整体反向。运行期错误现在表达为类型化
> z42 异常（`Std.InvalidMarshalException` 等），按 catch by class + 读
> `Message` / `StackTrace` 消费，不再需要跨语言 catalog。本归档作为
> 历史决策追溯保留，不删除。

> 状态：🟢 已完成 | 完成：2026-05-10 | 创建：2026-05-10
> 类型：feat + refactor（最小化模式）

**变更说明：** 把 5 个 Z#### VM runtime 错误码（散落在 9 个文件的 `anyhow!("Zxxxx: ...")` 字符串）集中到一个 catalog；与 C# 端通过共享 JSON 数据文件同步，让 `z42c explain Z0905` 与 `z42vm explain Z0905` 都返回同一份内容。

**原因：** 接续批 1 — `DiagnosticCatalog` 已能路由跨码空间，缺的是 Z 码的"内容来源"。共享 JSON 是 C# / Rust 双向消费 SoT 的最简方案（无 build-time 工具链耦合）。

**raw-throw 审计**（spec 中 10 处）正交，留给独立 spec。

## 阶段 1: SoT JSON

- [x] 1.1 [docs/error-codes/Z.json](../../docs/error-codes/Z.json) NEW — 5 entries (Z0905 / Z0906 / Z0907 / Z0908 / Z0910)，schema：`{ "entries": [{ "code", "title", "description", "example" }] }`
- [x] 1.2 schema 注释段：当新增 Z 码时改这个文件即可，C# / Rust 自动 pick up

## 阶段 2: Rust 端

- [x] 2.1 [src/runtime/src/diagnostics/mod.rs](../../src/runtime/src/diagnostics/mod.rs) NEW — `DiagnosticEntry` struct + `pub fn explain(code) -> Option<&DiagnosticEntry>` + `pub fn list_all()`
- [x] 2.2 catalog 数据：`include_str!("../../../docs/error-codes/Z.json")` + `serde_json` lazy parse via `OnceLock`
- [x] 2.3 [src/runtime/src/lib.rs](../../src/runtime/src/lib.rs) `pub mod diagnostics;`
- [x] 2.4 [src/runtime/src/diagnostics/registry_audit_tests.rs](../../src/runtime/src/diagnostics/registry_audit_tests.rs) — 单元测试遍历 `src/runtime/src/`，确保所有 `Z[0-9]{4}` 字面量都在 catalog 中（drift guard）

## 阶段 3: z42vm CLI

- [x] 3.1 [src/runtime/src/main.rs](../../src/runtime/src/main.rs) 加 `explain <code>` / `errors` 子命令（与 z42c 同结构）
- [x] 3.2 渲染：与 C# `DiagnosticCatalog.Explain` 同款样式（`error[CODE]: title` + `─` 分隔 + Description + Example）

## 阶段 4: C# 端 Z catalog

- [x] 4.1 [src/compiler/z42.Core/Diagnostics/RustErrorCatalog.cs](../../src/compiler/z42.Core/Diagnostics/RustErrorCatalog.cs) NEW — 镜像 `WorkspaceCatalog` 结构；从 embedded resource 读 JSON
- [x] 4.2 [src/compiler/z42.Core/z42.Core.csproj](../../src/compiler/z42.Core/z42.Core.csproj) `<ItemGroup>` 加 `<EmbeddedResource Include="..\..\..\docs\error-codes\Z.json" LogicalName="Z.json" />`
- [x] 4.3 模块初始化器 + 显式 Register 路径（与 WorkspaceCatalog 对称）
- [x] 4.4 移除 `DiagnosticCatalog.Explain` 中"central catalog pending"占位 hint

## 阶段 5: 测试

- [x] 5.1 [src/runtime/src/diagnostics/](../../src/runtime/src/diagnostics/) tests — explain 返回正确条目；catalog drift guard
- [x] 5.2 [src/compiler/z42.Tests/RustErrorCatalogTests.cs](../../src/compiler/z42.Tests/RustErrorCatalogTests.cs) — embedded resource load + 5 entries 都能取到 + 路由 `DiagnosticCatalog.Explain("Z0905")` 真返
- [x] 5.3 e2e smoke：`z42c explain Z0905` 与 `z42vm explain Z0905` 输出 byte-equivalent（除了 trailing whitespace）

## 阶段 6: 文档 + 验证

- [x] 6.1 [docs/error-codes/README.md](../../docs/error-codes/README.md) NEW — 解释 Z.json 是 SoT，C# / Rust 各自怎么消费，新增码流程
- [x] 6.2 [docs/design/testing/cross-platform-testing.md](../../docs/design/testing/cross-platform-testing.md) 或 testing.md 不需要更新（不影响测试架构）
- [x] 6.3 dotnet test 全绿
- [x] 6.4 cargo test 全绿
- [x] 6.5 test-vm.sh 全绿

## Scope

| 文件 | 类型 | 说明 |
|---|---|---|
| `docs/error-codes/Z.json` | NEW | SoT |
| `docs/error-codes/README.md` | NEW | 流程说明 |
| `src/runtime/src/diagnostics/mod.rs` | NEW | Rust catalog |
| `src/runtime/src/diagnostics/registry_audit_tests.rs` | NEW | drift guard |
| `src/runtime/src/lib.rs` | MODIFY | mod 声明 |
| `src/runtime/src/main.rs` | MODIFY | explain / errors 子命令 |
| `src/runtime/Cargo.toml` | MODIFY (if needed) | serde_json 已有？验证 |
| `src/compiler/z42.Core/Diagnostics/RustErrorCatalog.cs` | NEW | C# Z 镜像 |
| `src/compiler/z42.Core/z42.Core.csproj` | MODIFY | EmbeddedResource |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | MODIFY | 移除占位 hint |
| `src/compiler/z42.Tests/RustErrorCatalogTests.cs` | NEW | 测试 |

**只读引用：**
- `src/runtime/src/native/error.rs` 等 9 个 Z#### emit 站点 — 不动；仅 audit test 扫描

## 备注

- 共享 JSON 路径走相对路径，build / test 都能触达；CI 中也能正常 include
- 如果 C# 与 Rust 有微小 schema 差异（如字段命名 camelCase vs snake_case），用 serde rename + Newtonsoft attributes 各自适配
