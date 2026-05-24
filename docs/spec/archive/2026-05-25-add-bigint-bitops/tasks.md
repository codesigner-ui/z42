# Tasks: add bit operations to Std.Numerics.BigInt

> 状态：🟢 已完成 | 创建：2026-05-25 | 归档：2026-05-25 | 类型：feat (extend existing class)
> Spec 类型：minimal mode

**变更说明**：在 `Std.Numerics.BigInt` 上加位运算 API。Pure script，无 VM
改动。是 `add-bigint-modpow` 的前置（ModPow 用 ShiftRight + TestBit 遍历
exponent bits）。

## 选择 + 不在 scope 内的

| API | v0 | Why / Why not |
|-----|----|----|
| `And` / `Or` / `Xor` | ✅ 非负数双方 | crypto / bitset 主用例；非负数下 limb-wise op 直观 |
| `ShiftLeft(int n)` | ✅ n ≥ 0 | 保留符号；操作 magnitude（`-8 << 1 = -16`）|
| `ShiftRight(int n)` | ✅ n ≥ 0 | 保留符号；逻辑 magnitude shift（`-8 >> 1 = -4`，**非** .NET 的算术 shift） |
| `TestBit(int n)` | ✅ n ≥ 0, receiver 非负 | ModPow 需要 |
| `BitLength()` | ✅ | 返回需要的最少 bit 数（0 → 0, 1 → 1, 255 → 8 等）|
| `Not()` | ❌ 留 `bigint-future-bitops-twoscomp` | 两补码语义独立 spec |
| 负数 And/Or/Xor (两补码) | ❌ 留 follow-up | 全两补码语义独立 spec |
| `ShiftRight` 算术语义 (vs 逻辑) on 负数 | ❌ v0 用 magnitude shift；两补码 follow-up | 一致性 |

## API

```z42
public class BigInt {
    // ── 新增 ─────────────────────────────────────────────────────────────

    /// 位与；双方都 must 非负 (this._sign >= 0 AND other._sign >= 0)；
    /// 负数 throws ArgumentException(v0 限制).
    public BigInt And(BigInt other);

    /// 位或；同 And 的 non-negative 限制.
    public BigInt Or(BigInt other);

    /// 位异或；同 And 的 non-negative 限制.
    public BigInt Xor(BigInt other);

    /// 左移 n 位 (n >= 0)。Magnitude 左移；符号保留（`-8 << 1 = -16`）。
    /// n 为负数 throws ArgumentException.
    public BigInt ShiftLeft(int n);

    /// 右移 n 位 (n >= 0)。Magnitude 逻辑右移（非算术）；符号保留
    /// （`-8 >> 1 = -4`）。n 为负数 throws ArgumentException.
    public BigInt ShiftRight(int n);

    /// 测试 bit `n` (n >= 0) 是否 set。Receiver 必须非负（v0 限制）。
    /// `n` 超出 BitLength 返回 false。
    public bool TestBit(int n);

    /// 表示该数值（绝对值）所需的最少 bit 数。
    /// `Zero.BitLength() == 0`, `One.BitLength() == 1`, `255.BitLength() == 8`,
    /// `(-256).BitLength() == 9`. 返回 `>= 0`.
    public int BitLength();
}
```

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.numerics/src/BigInt.z42` | MODIFY | 加 6 个 public method + 2 internal helper（_magShiftLeft / _magShiftRight）|
| `src/libraries/z42.numerics/tests/bigint_bitops.z42` | NEW | And/Or/Xor + Shift + TestBit + BitLength 各 3+ 案例 |
| `docs/design/stdlib/numerics.md` | MODIFY | API 列表 + Deferred 段（two's-complement / Not 等）|

## Tasks

- [x] 1.1 `BigInt.z42`: `And` (magnitude limb-wise; result_length = min(la, lb))
- [x] 1.2 `BigInt.z42`: `Or` (magnitude limb-wise; result_length = max(la, lb))
- [x] 1.3 `BigInt.z42`: `Xor` (magnitude limb-wise; result_length = max(la, lb))
- [x] 1.4 `BigInt.z42`: `ShiftLeft(int n)`:
  - n == 0 → return this
  - 拆 n = limbShift * 31 + bitShift
  - 在 _mag 前面插入 limbShift 个 0 limb
  - 对剩余 bitShift 用 limb-wise rotate
- [x] 1.5 `BigInt.z42`: `ShiftRight(int n)`:
  - n == 0 → return this
  - n >= total_bits → return Zero（保留 sign 0）
  - 拆 limbShift + bitShift；丢弃低 limbShift 个 limb；剩余 bitShift right-rotate
- [x] 1.6 `BigInt.z42`: `TestBit(int n)`:
  - n < 0 throws ArgumentException
  - this._sign < 0 throws ArgumentException
  - limbIdx = n / 31, bitIdx = n % 31
  - limbIdx >= _mag.Length → return false
  - return (_mag[limbIdx] >> bitIdx) & 1 == 1
- [x] 1.7 `BigInt.z42`: `BitLength()`:
  - this._sign == 0 → return 0
  - 取 top limb，求其 floor(log2) + 1
  - 加 (mag.Length - 1) * 31
- [x] 2.1 `tests/bigint_bitops.z42` NEW:
  - And: 0xFF AND 0x0F == 0x0F; (2^100 - 1) AND 0xFFFFFFFF == 0xFFFFFFFF
  - Or: 0xF0 OR 0x0F == 0xFF
  - Xor: 0xFF XOR 0x0F == 0xF0
  - ShiftLeft: 1 << 10 == 1024; 1 << 100 vs Pow(2, 100)
  - ShiftRight: 1024 >> 10 == 1; (1 << 100) >> 50 == (1 << 50)
  - ShiftRight 大 n: 1 >> 1000 == 0
  - ShiftLeft/ShiftRight 符号保留: (-8 << 1) == -16; (-8 >> 1) == -4
  - TestBit: TestBit(0) on 1 == true; TestBit(1) on 1 == false; TestBit(99) on 2^100
  - BitLength: 0 == 0, 1 == 1, 255 == 8, 256 == 9, (1 << 100) == 101
  - Negative shift 抛 ArgumentException
  - Negative receiver TestBit 抛 ArgumentException
  - And/Or/Xor 负数操作数抛 ArgumentException
- [x] 3.1 `docs/design/stdlib/numerics.md` MODIFY:
  - v0 API 段加新 6 方法
  - Deferred 段 `bigint-future-bitops` 改名为 `bigint-future-bitops-twoscomp`
  - 加 `bigint-future-bitops-not` 单独条
- [x] 4.1 `./scripts/build-stdlib.sh` z42.numerics 编译通过
- [x] 4.2 `./scripts/test-stdlib.sh z42.numerics` 39 既有 + N 新 全过
- [x] 5.1 commit + push + archive

## 备注

- 不引入两补码语义 — BigInt v0 的 internal repr 是 sign + magnitude，不是 two's complement
- ShiftRight 在 BigInt 不是算术 shift（保留绝对值 magnitude 而非符号扩展）；明确文档化
