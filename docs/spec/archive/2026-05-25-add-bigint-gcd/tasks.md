# Tasks: add BigInt.Gcd + Lcm

> 状态：🟢 已完成 | 创建：2026-05-25 | 归档：2026-05-25
> 类型：feat (extend existing class) | Spec 类型：minimal mode

**变更说明**：在 `Std.Numerics.BigInt` 上加 `Gcd(BigInt other) → BigInt` 和
`Lcm(BigInt other) → BigInt`。Gcd 走 Euclidean (`gcd(a, b) = gcd(b, a mod b)`)，
操作绝对值，结果总非负。Lcm 用 `|a*b| / gcd(a, b)` 闭式。

**原因**：数论 / RSA key gen 等需要约简分数 / 计算公倍数；当前用户得用
`Pow + Mod` 自己拼，啰嗦且容易写错。

**算法（Euclidean GCD）**：
```
a = this.Abs()
b = other.Abs()
while !b.IsZero():
    r = a.Mod(b)
    a = b
    b = r
return a
```

**约定**：
- 操作数取绝对值，符号忽略；结果总 ≥ 0
- `gcd(0, 0) = 0`（数学惯例：`0` 是任何整数的倍数，但 `gcd` 无 well-defined 最大公约数 → 取 `0` 与 .NET / Python `math.gcd` 一致）
- `gcd(a, 0) = |a|`、`gcd(0, b) = |b|`
- `lcm(0, _) = 0`、`lcm(_, 0) = 0`（避免除零；与数学惯例一致）

**Out of scope（follow-up）**：
- `IsProbablyPrime(int witnesses)` / `NextPrime()`（需 RNG + Miller-Rabin，复杂度高）
- `ModInverse(BigInt modulus)`（扩展欧几里得，可作为 ModPow 负指数前置条件）
- Binary GCD (Stein's algorithm) —— 常数因子优化，遇瓶颈再换

**文档影响**：`numerics.md` 把 `bigint-future-gcd` 拆为
✅ `Gcd / Lcm` 已落地 + 新建 `bigint-future-prime` (`IsProbablyPrime` / `NextPrime`)
+ 新建 `bigint-future-modinverse` (扩展欧几里得)。

## Tasks

- [x] 1.1 `BigInt.z42`: `public BigInt Gcd(BigInt other)`
- [x] 1.2 `BigInt.z42`: `public BigInt Lcm(BigInt other)`
- [x] 2.1 NEW `tests/bigint_gcd.z42` — 18 tests (gcd basic / coprime / zero / negative / Fib adjacency / 2^100 multi-limb / lcm round-trip)
- [x] 3.1 `numerics.md`: split `bigint-future-gcd` → ✅ Gcd/Lcm landed + new `bigint-future-prime` + `bigint-future-modinverse` + `bigint-future-gcd-binary` follow-ups
- [x] 4.1 GREEN (stdlib scope: 116/116 files, 18/18 new gcd tests pass) + archive + commit + push

## 备注

GREEN 限定 `--scope=stdlib`（全部 116 stdlib 测试文件通过，含本变更新增的
18 个 gcd/lcm 用例）。`test-all.sh --scope=full` 在 VM goldens stage 失败，
失败原因是并行 session 的 z42.net K2 UDP 实现 WIP（`src/runtime/src/corelib/
network.rs` + `vm_context.rs` 字段添加 `udp_sockets` / `next_udp_socket_id`
尚未在所有 `VmCore::new` 调用点同步），与本变更（纯 z42 stdlib 源码，零
Rust 改动）解耦。
