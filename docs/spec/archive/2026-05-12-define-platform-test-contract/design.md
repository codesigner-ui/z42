# Design: 平台 facade 测试契约

## Architecture

```
                  host build.sh（host-only）
                  │
                  ▼  dotnet build src/compiler/z42.slnx
                  │  z42c examples/hello.z42 -o hello.zbc
                  │  z42c examples/multi_line.z42 -o multi_line.zbc
                  │
        ┌─────────┴─────────┬──────────────────┐
        ▼                   ▼                  ▼
   iOS:                Android:           wasm:
   Z42VM.xcframework/  z42vm/src/         pkg-{web,nodejs}/
   Resources/          androidTest/       + tests/fixtures/
   test-fixtures/      assets/
                       test-fixtures/

   XCTest harness      Instrumented Test  playwright
   依赖 Z42VM facade   依赖 z42vm AAR     依赖 @z42/wasm
        │                   │                  │
        ▼                   ▼                  ▼
       Tier 3 facade  ──→  Tier 1 C ABI (z42_host_*)
                                  │
                                  ▼
                              libz42 (runtime)
```

测试只走右下角的实线箭头；测试代码内**不出现** `z42c` / `dotnet` 调用，对应到本仓库的硬性 invariant。

## Decisions

### Decision 1: contract 文档化 vs 跨平台共享 test runner

**问题：** 三个平台用的 test 框架天差地别（XCTest / JUnit / playwright）。要 contract"共享"，是写成 markdown 契约让三平台手工对齐，还是搞一个跨平台 declarative test runner（如 YAML 描述 scenario，per-platform adapter 生成）？

**选项：** 
- **A. Markdown contract** — 本 spec 走的路。每个 scenario 编号 R1–R7，下游 spec 写 XCTest/JUnit/playwright 代码时手工对照命名。优：实现简单、读者直观；劣：人工对齐易漂移。
- **B. YAML declarative + adapter** — 写一个 `tests/contract.yaml`，每平台 adapter 读 YAML 产 test 代码。优：自动对齐；劣：adapter 是新建 toolchain，工作量远大于 z42 本身的 test runner。

**决定：** A。z42 目前 4 个平台、7 个 scenario、单次拉齐成本低；B 是 over-engineering，等平台 / scenario 翻番再考虑。

### Decision 2: fixture 的最小集

**问题：** 每个 scenario 是否要独立 fixture？最少几个？

**选项：** 
- **A. Per-scenario fixture** — 每个 scenario 一个 `.zbc`：smoke / multi_line / arg_mismatch / ... 共 7 个。
- **B. 复用 fixture** — smoke `hello.zbc`、multi-line `multi_line.zbc`、garbage 字节数组 inline；arg_mismatch / entry_not_found / resolver 错误都用 hello.zbc 走错误调用路径覆盖。

**决定：** B。fixture 数量从 7 → 2 + 1 inline byte array；显著降低 build.sh 复杂度，三平台 ship 体积一致。

### Decision 3: stdout sink contract — `Data` / `ByteArray` / `Uint8Array`，per call 是 line 还是 chunk？

**问题：** Tier 1 sink 收到的是 z42 runtime 每次 `Console.WriteLine` 触发一次的字节包（含 `\n`）。三个平台 facade 各自把这个字节包包装成 Data / ByteArray / Uint8Array 回调一次。Test 怎么写"按 line 收"？

**选项：** 
- **A. 在 contract 里强行规定 "1 call = 1 line"** — 要求 facade 不 buffering 不拼合
- **B. contract 只规定 "字节累积顺序 == 输出顺序"** — 累积后 == `"a\nb\nc\n"`；不约束 callback 次数

**决定：** B。底层 host_tests.rs 测的就是"顺序"，不测 1-call-per-line（runtime 在不同的 flush 策略下可能合并）。

### Decision 4: 不在本 contract 测 threading

**问题：** 草稿期曾考虑加 R8 "后台线程 invoke + 主线程收 sink"，针对 iOS / Android。

**决定：** **不纳入**。v0.1 runtime 是单实例 + 同步 invoke，threading 还没有正式语义，强测出来的契约会随后续 threading 设计推翻。该 scenario 进 spec.md **Deferred / Future Work** 段，等 threading 落地（roadmap 后续 phase）再回来补。

**影响：** 三个下游 spec 的实施清单不必处理 threading；R7 总数 = 7。

## Implementation Notes

### 编译 fixture 的 build.sh 改动模板（每个平台落地时复用）

```bash
# host 端，在 build.sh 早期执行（compiler 已就绪后）
"$Z42C" "$REPO/examples/hello.z42" -o "$STAGE/test-fixtures/hello.zbc"
"$Z42C" "$REPO/examples/multi_line.z42" -o "$STAGE/test-fixtures/multi_line.zbc"

# 复制进各自 bundle / asset / npm 包
# iOS:     cp -r "$STAGE/test-fixtures" "$XCFRAMEWORK/Resources/"
# Android: cp -r "$STAGE/test-fixtures" "$AAR_BUILD/src/androidTest/assets/"
# wasm:    cp -r "$STAGE/test-fixtures" "$PKG_OUT/test-fixtures/"
```

需要在 [examples/](../../../../examples/) 新增 `multi_line.z42`（3 行 Console.WriteLine）。这个新增可以放进 `define-platform-test-contract` 本 spec 还是下游 spec？**决定：放第一个落地的下游 spec 里**（whichever 先开工，比如 `add-ios-tests`），避免 contract spec 引入实施侧改动。

### 错误码 → 平台异常映射的统一描述

每个平台 facade 已经定义了 platform-specific 异常类（`Z42VMError` / `Z42VMException`）。Contract 不重新发明这些；只约束 "status N → 该平台 spec 第 §x 节定义的对应符号"。

## Testing Strategy

本 spec 不带任何代码改动，因此**没有可执行 test**。验证手段：

- 评审：proposal + spec + design 三份文档语义自洽
- 形式：scenario 标号 R1–R8 在下游三个 spec 的 tasks.md 中**都能找到对应 task**
- 同步：[docs/design/runtime/embedding.md](../../../design/runtime/embedding.md) 加 "host 编译 / mobile 仅运行" 段落；[src/toolchain/host/platforms/README.md](../../../../src/toolchain/host/platforms/README.md) 顶部加 fixture 预编原则
