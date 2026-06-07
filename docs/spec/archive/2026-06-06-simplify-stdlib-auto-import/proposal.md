# Proposal: 标准库自动可用，无需在 manifest 声明

## Why

用户写 z42 程序时，凡用到 `Std.IO` / `Std.Math` / `Std.Test` 等都习惯性地在
`z42.toml` 的 `[dependencies]`（或 `[tests.dependencies]`）里声明对应包——这是冗余的
"手动配置"。

**实测证明：stdlib 本来就是自动可用的，声明纯属多余。** 一个 `[dependencies]` 为空的项目，
`using Std.IO; using Std.Math;`、甚至用 z42.test 独有的 `new Bencher(...)`，都**编译通过、零报错**。

机制（现状）：

- **编译期**：[`PackageCompiler.BuildTarget.cs:333`](../../../../src/compiler/z42.Pipeline/PackageCompiler.BuildTarget.cs) + [`Helpers.cs:158`](../../../../src/compiler/z42.Pipeline/PackageCompiler.Helpers.cs) —— `ScanLibsForNamespaces` 与 `BuildDepIndex` 对 `meta.Name.StartsWith("z42.")` 的包**无条件可见**，与声明无关。
- **运行期**：`build_host_module` 按 `import_namespaces` 扫 NSPC 解析（`drop-index-json-self-describing` 之后），**不看项目 DEPS 元数据**。

这与 Rust 一致：从不在 `Cargo.toml` 声明 `std`，`use std::...` 直接可用，std 跟工具链版本走。
z42 已是这个模型，只是没明说、且约定/示例/lint 在反向鼓励声明。

## What Changes

把"stdlib 自动可用"从**隐式机制**提升为**显式、文档化、有 lint 守护的约定**（方向 A）：

1. **文档**：在 `docs/design/compiler/project.md` 明确"`Std.*` / `z42.*` 是工具链自带、无需声明；
   `[dependencies]` / `[tests.dependencies]` 只用于**第三方**依赖"。
2. **命名保留**：`Std.*` 命名空间保留给官方 stdlib，禁止第三方包占用（防止 shadow/冲突）。
   （编译期检测：第三方包声明 `namespace Std.*` → 报错。）
3. **lint 反转**：新增一条 WS 警告——**项目声明了 stdlib 依赖（冗余，可删）**。把现状"鼓励声明"
   翻成"鼓励不声明"。与已有 WS012（dev-dep 放错位）配套但方向相反。
4. **清理冗余声明**：移除示例 / 模板里用户项目的 stdlib `[dependencies]`。
   **注意**：stdlib 包**自己**的 inter-deps（z42.io → z42.time）**保留**——那是 workspace
   build 排序用的（编 z42.io 需 z42.time.zpkg 先在），不是用户面。
5. **`[tests.dependencies] z42.test` 复议**（与 `add-tests-bench-manifest-config` 对齐点）：
   该声明对编译冗余（z42.test 自动加载已证）。其唯一作用是 zpkg DEPS 元数据记录——而
   NSPC 自描述后元数据不参与解析。是否清理那 21 处需与该 spec 作者对齐。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/design/compiler/project.md` | MODIFY | stdlib 自动可用约定 + `Std.*` 保留 |
| `src/compiler/z42.Project/...`（lint/WS codes） | MODIFY | 新增"冗余 stdlib 声明" WS 警告 + `Std.*` 保留检测（具体文件待 design 定位） |
| `src/compiler/z42.Tests/...` | NEW/MODIFY | lint + 保留命名空间的单测 |
| `examples/*.z42.toml`（含 stdlib 声明的） | MODIFY | 删冗余 stdlib `[dependencies]` |
| `docs/design/stdlib/overview.md` 或 README | MODIFY | 用户向"无需声明 stdlib"说明 |

> 精确文件清单待 design 阶段定位 lint 代码所在（WS 码注册表 + manifest 校验入口）。

## Out of Scope

- **stdlib 包之间的 inter-deps**：保留（workspace build 排序需要）。
- **第三方依赖声明**：不变（仍需在 `[dependencies]` / `[tests.dependencies]` 声明）。
- **zpkg DEPS 元数据是否彻底不记 stdlib**：单列（涉及 packaging / 可能的 lockfile），本次只做约定 + lint + 文档。
- **`add-tests-bench-manifest-config` 的 `[tests.dependencies] z42.test`**：是否清理那 21 处需与该作者对齐，不在本次强行改。

## Open Questions（已裁决 2026-06-06）

- [x] WS 码编号 → **WS013**（WS013-019 空号段的首个）。
- [x] `Std.*` 保留强度 → **硬错误（E0605）**：保留语义要硬，否则 "Std.* 一定是官方 stdlib" 假设不成立。
- [x] `[tests.dependencies] z42.test` 那 21 处 → **本次一并清掉**（阶段 3）。

## 证据（已本地验证）

- 空 `[dependencies]` + `using Std.IO; using Std.Math; Math.Sqrt(2)` → 编译成功（仅 WS009 冗余 entry 警告）。
- 空 `[dependencies]` + `new Bencher(1,1); BenchHelpers.blackBox(42)`（z42.test 独有）→ 编译成功，无"未声明"报错。
- 代码：`BuildDepIndex` / `ScanLibsForNamespaces` 对 `z42.*` 的 isStdlib 旁路；无 lint 强制声明（WS008=未知 key / WS009=冗余 entry / WS012=dev-dep 放错位，均不要求声明）。
