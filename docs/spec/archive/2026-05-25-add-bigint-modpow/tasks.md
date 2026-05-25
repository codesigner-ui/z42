# Tasks: add BigInt.ModPow (modular exponentiation)

> 状态：🟢 已完成 | 创建：2026-05-25 | 归档：2026-05-25
> 类型：feat (extend existing class) | Spec 类型：minimal mode

**变更说明**：在 `Std.Numerics.BigInt` 上加 `ModPow(BigInt exp, BigInt modulus) → BigInt`，
计算 `(this^exp) mod modulus`。利用 `add-bigint-bitops` (2026-05-25) 落地的
`TestBit` + `ShiftRight` 走 square-and-multiply (LSB-first)，把指数大小为
N 的爆炸 Pow→Mod 操作降到 O(N) 次模乘。RSA / DH / Diffie-Hellman 必需。

**原因**：`Pow` workaround (`a.Pow(b).Mod(m)`) 对大指数指数爆炸（`a^b`
中间结果指数长），只 toy 用例可用。

**算法（square-and-multiply, LSB-first）**：
```
result = 1
b = this.Mod(modulus)
e = exp
while !e.IsZero():
    if e.TestBit(0):
        result = (result * b).Mod(modulus)
    b = (b * b).Mod(modulus)
    e = e.ShiftRight(1)
return result
```

**v0 限制**：
- `modulus > 0`，否则 `ArgumentException`
- `exp >= 0`（负指数需要 modular inverse，留 follow-up）
- `exp == 0` → result `1`（mathematical convention，含 `0^0 mod m == 1`）
- `modulus == 1` → 总是 `0`

**Out of scope**：
- Montgomery reduction / Barrett reduction（v0 直接走 `Multiply().Mod()`，常数因子高但正确；优化留 follow-up）
- 负指数（modular inverse）

**文档影响**：`numerics.md` flip `bigint-future-modpow` Deferred → ✅ landed。

## Tasks

- [x] 1.1 `BigInt.z42`: `public BigInt ModPow(BigInt exp, BigInt modulus)`
- [x] 2.1 NEW `tests/bigint_modpow.z42` — basic / large exp / edge cases (modulus==1, exp==0, base==0)
- [x] 3.1 `numerics.md`: flip `bigint-future-modpow` Deferred → ✅ landed
- [x] 4.1 GREEN + archive + commit + push
