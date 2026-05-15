# Tasks: add z42.random

> 状态：🟢 已完成 | 创建：2026-05-15 | 完成：2026-05-15 | 类型：feat
> Spec 类型：minimal mode（per workflow.md 纯 stdlib，与 z42.encoding / z42.time / z42.toml 同款 lighter spec）

## 实施期发现

1. **z42 lexer 暂不支持 hex 字面量**（`0xFFFFFFFF` 不解析），全部 mask 用十进制 named const（`long MASK32 = 4294967295`）。同 z42.toml 数字 parser 走十进制路径的限制。
2. **z42 primitive `int[]` 不零初始化**：`new int[N]` 后元素是 Null，`counts[i] + 1` → "type mismatch: Null vs I64"。其他 stdlib 类只写不读已掩盖；本 spec 第一次撞到（测试代码 must 显式 init loop）。
3. **`u32` 是 z42 reserved keyword**：本地变量命名 `u32` 报 "unexpected token"。改名 `raw32`。所有数值别名（`u8 / u16 / u32 / u64 / i8 / ... / isize / usize`）均保留。
4. **`(int) long` cast 不截断**：z42 int 内部存 I64，cast 是 type-tag-only，不丢高位。要 32-bit 截断必须显式 `& MASK32`。

以上 4 点均记录到 [docs/design/stdlib/random.md](../../design/stdlib/random.md) Deferred / 实施期发现段。

## 背景

stdlib roadmap P1 表里的 `z42.random`：通用伪随机数。z42.test 的 fixture 生成、用户测试代码、build script 临时 ID 等都需要简单 PRNG。L1，纯脚本，无依赖。

C# `System.Random` 与 Rust `rand::rngs::SmallRng` 对标，但本期只做最小 API：seeded LCG/xorshift/PCG 的一种，按 seed 决定 deterministic 输出。安全随机（CSPRNG）走 z42.crypto，独立 spec。

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 算法 | (a) Linear Congruential / (b) xorshift64 / (c) PCG-XSH-RR | (c) PCG-XSH-RR | 状态 64 bit；输出质量好（passes PractRand 32TB）；纯 i64 + 位运算可表达 |
| 2. State 表达 | (a) class instance / (b) struct | (a) Random class | 同 C# `System.Random`；可变 state via instance field |
| 3. Seed 类型 | (a) int / (b) long | (b) long | PCG 状态是 64-bit；long 直接成为 seed |
| 4. Default seed | (a) 固定 / (b) wall-clock ms | (b) | `new Random()` 用 `__time_now_ms` 作 seed；`new Random(seed)` 用显式 seed |
| 5. API surface | NextLong / NextInt / NextBool / NextDouble / NextRange | full | C# parity |
| 6. Thread-safety | (a) safe / (b) not safe | (b) | z42 当前单线程；多线程到 z42.threading 时再设计 |

## 阶段 1: 包骨架

- [ ] 1.1 NEW `src/libraries/z42.random/z42.random.z42.toml` — manifest，dep on z42.core
- [ ] 1.2 NEW `src/libraries/z42.random/src/Random.z42`
  - `namespace Std.Random;`
  - `public class Random` with PCG-XSH-RR state
  - `Random()` no-arg → seed from wall-clock
  - `Random(long seed)` explicit
  - `NextLong()` → full i64
  - `NextInt()` → low 32 bits as int
  - `NextLong(long min, long max)` → range, max exclusive
  - `NextDouble()` → [0.0, 1.0)
  - `NextBool()` → coin flip
  - private `_step()` — PCG core step

## 阶段 2: 测试

- [ ] 2.1 NEW `src/libraries/z42.random/tests/random_basic.z42`
  - Same seed → identical sequence (determinism)
  - Different seed → different first value
  - NextBool 1000 次 ~50% true/false
  - NextDouble 在 [0, 1) 范围
  - NextLong(0, 10) 在 [0, 10) 范围
- [ ] 2.2 NEW `src/libraries/z42.random/tests/random_distribution.z42`
  - Chi-square smoke: 10000 buckets，标准差 reasonable
  - NextRange(0, n) 每个 bucket 计数 ~ 总数 / n

## 阶段 3: Wiring + docs

- [ ] 3.1 MODIFY `src/libraries/z42.workspace.toml` 加 `"z42.random"`
- [ ] 3.2 MODIFY `scripts/build-stdlib.sh` 加 LIBS + index.json `Std.Random`
- [ ] 3.3 NEW `src/libraries/z42.random/README.md`
- [ ] 3.4 NEW `docs/design/stdlib/random.md` — 简短设计 + Decisions + Deferred（CSPRNG / multi-thread / seed entropy）
- [ ] 3.5 MODIFY `docs/design/stdlib/roadmap.md` — z42.random 从 P1 表移到 "已落地"
- [ ] 3.6 MODIFY `docs/design/stdlib/organization.md` — 加 z42.random 行
- [ ] 3.7 MODIFY `src/libraries/README.md` — 包列表加 z42.random

## 阶段 4: GREEN + 归档

- [ ] 4.1 `./scripts/build-stdlib.sh` 全绿
- [ ] 4.2 `./scripts/test-stdlib.sh z42.random` 全绿
- [ ] 4.3 `./scripts/test-stdlib.sh` 整体不回归（36 file? +1 file）
- [ ] 4.4 mv `docs/spec/changes/add-z42-random/` → `docs/spec/archive/2026-05-15-add-z42-random/`
- [ ] 4.5 commit + push
