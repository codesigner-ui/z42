# workflow/testing/

z42 各层级测试的运行命令。**测试设计**（attribute 体系、TIDX section、runner 协议）见 [`docs/design/testing/`](../../design/testing/)。

## 测试层级

z42 测试分四层，各层独立运行：

| 层 | 跑什么 | 命令 |
|---|---|---|
| **C# 编译器单测** | xUnit on `z42.Tests/` — lexer / parser / type-check / IR-gen 单元 | [`unit-tests.md`](unit-tests.md) |
| **VM golden** | `src/tests/**/source.z42` 端到端（interp + JIT 双模） | [`vm-tests.md`](vm-tests.md) |
| **stdlib `[Test]`** | `src/libraries/<lib>/tests/*.z42` 经 z42-test-runner | [`stdlib-tests.md`](stdlib-tests.md) |
| **cross-zpkg** | 多 zpkg 协作（target lib + ext lib + main app） | [`cross-zpkg.md`](cross-zpkg.md) |

## 增量测试

[`changed-only.md`](changed-only.md) — `just test-changed` 根据 `git diff` 只跑受影响的测试命令集合（dev 内循环加速）。

## GREEN 门禁

CI 全绿门禁（`dotnet build` + `cargo build` + 上面 4 层全过）的定义见 [`../ci.md`](../ci.md)；规则在 [`.claude/rules/workflow.md`](../../../.claude/rules/workflow.md) 阶段 8。

## 一键全跑

```bash
just test        # 全部 4 层
just ci          # = build + test，CI 标准管线
```

## Scope-aware test-all（add-test-split-by-area, 2026-05-21）

`./scripts/test-all.sh` 默认跑 6 stages（dotnet build / cargo build /
dotnet test / test-vm / cross-zpkg / stdlib）≈ 3-5 min。iteration 期常
只改一个 area；用 `--scope=<value>` 跳过不相关 stages：

| Scope | 跑的 stages | 何时用 |
|-------|------------|--------|
| `full`（默认）| 全 6 | commit 前最终 GREEN，PR gate |
| `runtime` | cargo build + test-vm + cross-zpkg + stdlib | 改 `src/runtime/**` |
| `compiler` | dotnet build + dotnet test + test-vm + cross-zpkg + stdlib | 改 `src/compiler/**` |
| `stdlib` | test-vm + cross-zpkg + stdlib | 改 `src/libraries/**` / `examples/**` |
| `docs-only` | 0 stages（clean exit） | 仅 `docs/**` / `.claude/**` |
| `auto` | 由 `git diff --name-only HEAD` 推断 | 让脚本判断 |

`auto` 路径分类：

- `src/compiler/**` → `compiler`
- `src/runtime/**` + `src/tests/**` → `runtime`
- `src/libraries/**` + `examples/**` → `stdlib`
- `docs/**` + `.claude/**` → 不升级（默认 `docs-only`）
- 其他（Cargo.toml / .csproj / .github / 顶层 / scripts/）→ `full`（保守）
- compiler + runtime 都改 → `full`

例：

```bash
./scripts/test-all.sh --scope=runtime     # 跳 dotnet stages，约省 30-40s
./scripts/test-all.sh --scope=stdlib      # 跳 build + dotnet test，约省 60-90s
./scripts/test-all.sh --scope=auto        # 自动判断当前 diff
./scripts/test-all.sh                      # full，commit 前必走
```

**GREEN gate 规则**：iteration 期允许缩窄 scope 加速。**commit 前最终
GREEN 必须 `--scope=full`**（或 `--scope=auto` 自动等价 full，
即未缩窄）。Partial scope 不算 GREEN，不可作为 commit 通过依据。
详见 [`.claude/rules/workflow.md`](../../../.claude/rules/workflow.md) 阶段 8。
