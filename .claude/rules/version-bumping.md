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

修改 `.zbc` wire format（新 opcode / 新 section / 已定义 section 字段语义变化）时，**单次 commit 必须同步以下 4 处**，否则 `FormatGoldenTests` / `FormatInvariantTests` 任一会 fail：

1. **`src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs`** — `VersionMinor++` 并在常量旁注释本次 bump 的内容（参考已有行的格式）
2. **`src/runtime/src/metadata/zbc_reader.rs`** — `ZBC_VERSION_MINOR` 同步到新值
3. **`docs/design/runtime/zbc.md`** — "Minor changelog" 表加一行（minor / 日期 / 触发 spec / 引入内容）
4. **`src/tests/zbc-format/generate-fixtures.sh`** 跑一遍 — 6 个 fixture 的 `source.zbc` + `expected.json` 全部 regen，git diff 显示出格式 delta

提交前自检：

```bash
./scripts/check-versions-drift.sh    # （未来扩展时校 ZbcWriter/Reader 一致性）
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
