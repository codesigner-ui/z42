---
paths:
  - "src/compiler/z42.IR/BinaryFormat/**"
  - "src/compiler/z42.Project/**"
  - "src/runtime/src/metadata/**"
  - "docs/design/runtime/zbc.md"
  - "docs/design/runtime/zpkg.md"
  - "src/tests/zbc-format/**"
  - "src/tests/zpkg-format/**"
---

# `.zbc` / `.zpkg` minor version bump checklist

> z42 pre-1.0 strict-pin 政策：reader 精确匹配 writer 的 major + minor。
> 兼容性原则（"不为旧版本提供兼容"）见 [philosophy.md](philosophy.md#不为旧版本提供兼容2026-04-26-强化)。
>
> 这份文件只回答一个问题：bump version 时**具体要同步改哪些文件**才能让 invariant CI 通过。

---

## Bumping `.zbc` minor version（freeze-zbc-v1, 2026-05-14）

修改 `.zbc` wire format（新 opcode / 新 section / 已定义 section 字段语义变化）时，**单次 commit 必须同步以下 5 处**，否则 `FormatGoldenTests` / `FormatInvariantTests` / z42c zbc 单测任一会 fail：

1. **`src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs`** — `VersionMinor++` 并在常量旁注释本次 bump 的内容（参考已有行的格式）
2. **`src/runtime/src/metadata/zbc_reader.rs`** — `ZBC_VERSION_MINOR` 同步到新值
3. **`docs/design/runtime/zbc.md`** — "Minor changelog" 表加一行（minor / 日期 / 触发 spec / 引入内容）
4. **`src/tests/zbc-format/generate-fixtures.sh`** 跑一遍 — 6 个 fixture 的 `source.zbc` + `expected.json` 全部 regen，git diff 显示出格式 delta
5. **z42c 自举 writer 同步（port-z42c-zbc-writer 起，2026-06-10）**：
   - `src/z42c/z42c.ir/src/BinaryFormat/ZbcFormat.z42` — `ZbcVersion.Minor` 同步到新值（+注释）；若 bump 改了 ZW 已实现的 section 布局（TYPE/SIGS/FUNC/REGT 等），`ZbcWriter.z42` 的对应 BuildXxx 同步镜像
   - `src/z42c/z42c.semantics/tests/zbc/zbc_tests.z42` — golden hex 随 fixture regen 更新（test_zbc_empty_byte_identical 的 247B 串重截自 regen 后的 `src/tests/zbc-format/empty/source.zbc`：`xxd -p src/tests/zbc-format/empty/source.zbc | tr -d '\n'`）
   - 验证：`z42 xtask.zpkg test compiler-z42`（z42c zbc 单元须绿）

提交前自检：

```bash
z42 xtask.zpkg deps check    # （未来扩展时校 ZbcWriter/Reader 一致性）
./src/tests/zbc-format/generate-fixtures.sh
dotnet test --filter "FullyQualifiedName~Z42.Tests.Zbc"
```

由于 strict-pin 政策（reader 精确匹配 writer 的 major + minor），minor bump 必然让所有现存 `.zbc` artifacts 失效；常配套跑 `z42 xtask.zpkg regen` 把 stdlib + 测试 zbc 一并 regen。这是预期行为，不需要兼容代码。

如果只是修 reader / writer 的非格式相关 bug（不改 wire layout）— **不要** bump minor；invariant tests 仍会通过。

### zpkg 联动规则（freeze-zpkg-v0, 2026-05-14）

zbc minor bump **必须**同步 bump zpkg minor。除上述 4 步外加：

5. **`src/compiler/z42.Project/ZpkgWriter.cs`** — `VersionMinor++` 且注释更新内嵌 zbc 版本
6. **`src/runtime/src/metadata/zbc_reader.rs`** — `ZPKG_VERSION_MINOR` 同步
7. **`docs/design/runtime/zpkg.md`** — Minor changelog 加一行（指明触发 spec = 同次 zbc bump 的 spec）
8. **`src/tests/zpkg-format/generate-fixtures.sh`** 跑一遍 regen

提交前自检扩展：

```bash
./src/tests/zbc-format/generate-fixtures.sh
./src/tests/zpkg-format/generate-fixtures.sh
dotnet test --filter "FullyQualifiedName~Z42.Tests.Zbc|FullyQualifiedName~Z42.Tests.Zpkg"
```

---

## Bumping `.zpkg` minor version (independent)

仅改 zpkg outer（不动 zbc）时（如新增 zpkg-only section / 已定义 section 字段语义）：只触上述步骤 5-8（zpkg writer / Rust 常量 / zpkg.md changelog / zpkg fixture regen），跳过 zbc 步骤 1-4。

注意：实际工作中 zpkg-only 改动非常罕见（v0 历史里所有 minor bump 都耦合 zbc），但若发生，本节给出独立路径定义。

---

## bump 与 xtask↔nightly bootstrap 循环（opt-xtask-bootstrap-stdlib, 2026-06-04）

CI 的 `xtask-bootstrap` composite **下载上一次的 nightly**（`install-z42` → `.z42/`）来
编译 + 运行 xtask（vm-jit / bench / bench-update 三个 job 这样做）。所以 zbc/zpkg
minor bump 后会短暂出现循环：

- 旧 nightly 的 z42vm 是旧 zbc reader → 跑不了用**新** z42c 编出的 xtask.zpkg（strict-pin 失败）；
  且 xtask 现在还**对着 `.z42/libs`（旧 nightly stdlib）编译**，新 stdlib API 也可能缺。
- 于是 vm-jit / bench **红**，直到存在一个兼容的新 nightly——而产出它的正是 publish-nightly。

**为什么不死锁（自愈设计）**：`publish-nightly` 的 `needs` **只含从当前源码构建的 job**
（`build-and-test` 用 cargo+z42c 从源码 bootstrap xtask；`package-ios/android/wasm` 用源码
`z42 xtask.zpkg build stdlib` + `z42 xtask.zpkg build package`），**绝不依赖 download-bootstrap 的 vm-jit / bench**。所以
bump commit 推上 main 后：源码 job 全绿 → publish-nightly 发布新 nightly → 下一次 run 的
vm-jit / bench 下到新 nightly → 自愈。bump 当次那一跑 vm-jit/bench 红是预期的、一次性的。

> **硬约束**：任何 feed `publish-nightly` 的 job 必须从**当前源码** bootstrap（不许走
> download-nightly composite）。`package.sh` 移植进 xtask（Phase 5，已完成）后，package job 仍要
> `cargo build` + 源码编 xtask.zpkg，**不要**改用 `xtask-bootstrap` composite——否则 publish
> 路径变成依赖旧 nightly，死锁复活。

**手动发布 nightly（escape hatch）**：若自愈不及时（或要在不推 commit 的情况下刷新 nightly），
手动触发 CI 的 `workflow_dispatch`，它会从当前 main 源码构建并发布 nightly：

```bash
gh workflow run CI --ref main          # 或 Actions 页面 "Run workflow" 按钮
```

publish-nightly 的 `if` 已放行 `workflow_dispatch`；vm-jit/bench 即使红也不挡发布。
