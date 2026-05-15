# z42.random — Deterministic pseudo-random number generator

> 落地版本：2026-05-15（add-z42-random）
> 包路径：`src/libraries/z42.random/`
> 命名空间：`Std.Random`

## 职责

通用 deterministic PRNG，用于测试 fixture、生成临时 ID、shuffling、UI 抖动等场景。

**不是 CSPRNG**：不适合密码学 / 安全令牌 / session id。安全随机走 `z42.crypto`（独立 spec）。

## 算法选择：PCG-XSH-RR

参考 [pcg-random.org](https://www.pcg-random.org/)。State 64-bit，每次步进
`state' = state * MUL + INC`（LCG），输出 32-bit by
`rotr32(((state >> 18) ^ state) >> 27, state >> 59)`。

**为什么不是其他算法**：

| 候选 | 选 / 不选 | 理由 |
|------|----|------|
| Linear Congruential（C `rand`）| ❌ | 低位 0/1 周期严重，分布不均 |
| xorshift64 | ❌ | 没有 LCG 步进，PractRand 不通过；状态全 0 失败 |
| Mersenne Twister | ❌ | 状态 624 word（~2.5KB），对纯脚本 stdlib 太重 |
| **PCG-XSH-RR** | ✅ | 状态 64-bit，passes PractRand 32TB，码量小 |
| ChaCha20 | ❌ | CSPRNG，留 z42.crypto |

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. State | (a) 32-bit / (b) 64-bit | (b) | PCG-XSH-RR 标准；z42 i64 原生 |
| 2. Default seed | (a) 固定 / (b) wall-clock | (b) | C# parity；用户也可显式 seed |
| 3. Thread-safety | (a) safe / (b) unsafe | (b) | z42 单线程；z42.threading 时再做 |
| 4. Modulo bias 处理 | (a) ignore / (b) rejection sampling | (a) | 对 span ≪ 2^32 偏差 < 1e-7，v0 可接受 |
| 5. NextDouble 精度 | (a) 32-bit / (b) 53-bit | (b) | 完整 double mantissa |
| 6. Hex 字面量 workaround | inline 十进制 + named const | — | z42 lexer 暂不支持 hex `0xFFFFFFFF` 字面量（与 z42.toml 数字解析同款限制） |

## 不支持（Deferred）

### random-future-csprng
- **来源**：安全场景（session token / API key / Web 加密）
- **触发原因**：CSPRNG 需 syscall（`getrandom` / `BCryptGenRandom`）+ OS HAL
- **触发条件**：z42.crypto 落地时一并设计

### random-future-thread-safe
- **来源**：多线程并发使用同一 Random 实例
- **前置依赖**：z42.threading（Arc + atomic CAS 状态更新）
- **当前 workaround**：每线程独立 instance

### random-future-distributions
- **来源**：Normal / Gaussian / Exponential 等非均匀分布
- **触发条件**：用户场景（科学计算 / 模拟）实际需要时
- **当前 workaround**：Box-Muller 转换可纯脚本实现

### random-future-seed-from-entropy
- **来源**：从 OS entropy 源（`/dev/urandom`）取强 seed
- **触发原因**：wall-clock seed 可预测（PoC 攻击）；安全场景需要
- **触发条件**：与 random-future-csprng 同步

### random-future-stream-id
- **来源**：PCG 支持 multiple streams（不同 increment → 不相关序列）
- **触发原因**：v0 用固定 increment，足够 90% 场景

## 跨 stdlib 交互

- 依赖 `z42.core`（基础类型、`ArgumentException`）
- 依赖 `z42.time`（`DateTime.UtcNow().UnixMs()` 提供 wall-clock seed）
- 被 z42.test 可能使用作 fixture 生成（follow-up）

## 实施期发现（已记录到归档 tasks.md）

1. **z42 lexer 暂不支持 hex 字面量**（`0xFFFFFFFF` 不解析），全部 mask 用十进制 named const（`long MASK32 = 4294967295`）。同 z42.toml 数字 parser 走十进制路径的限制。考虑独立 spec 加 hex/oct/bin 字面量支持。
2. **z42 primitive `int[]` 不零初始化**：`new int[N]` 后元素是 Null，`counts[i] + 1` → "type mismatch: Null vs I64"。测试代码 must 显式 init loop。其他类 (TomlValue 等) 只写不读已掩盖此问题，本 spec 第一次撞到。Backlog item 候选：要么 zero-init by language，要么 doc 显式说"primitive arrays not zero-initialized, init before read"。
3. **`u32` 是 z42 reserved keyword**：本地变量命名 `u32` 报 "unexpected token"。改名 `raw32`。z42 数值别名（`u8 / u16 / u32 / u64 / i8 / ... / isize / usize`）均保留。
4. **`(int) long` cast 不截断**：z42 int 内部存为 I64，cast 到 `int` 是 type-tag-only 操作，不丢高位。要 32-bit 截断必须显式 `& MASK32`。同 add-std-process `convert_value` 识别 reference-type identity cast 那条路径的另一面。
