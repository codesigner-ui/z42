# Spec: Std.Crypto.SecureRandom — CSPRNG

## ADDED Requirements

### Requirement: GetBytes returns OS-CSPRNG bytes

#### Scenario: 正常请求
- **WHEN** `Std.Crypto.SecureRandom.GetBytes(32)` 调用
- **THEN** 返回长度恰为 32 的 `byte[]`；每个 byte ∈ [0, 255]；连续两次调用结果不同（除非天文巧合）

#### Scenario: n = 0
- **WHEN** `Std.Crypto.SecureRandom.GetBytes(0)` 调用
- **THEN** 返回长度 0 的 `byte[]`，无 throw

#### Scenario: n 负数
- **WHEN** `Std.Crypto.SecureRandom.GetBytes(-1)` 调用
- **THEN** throw `Std.ArgumentException`，message 含 "non-negative"

#### Scenario: 大 buffer
- **WHEN** `Std.Crypto.SecureRandom.GetBytes(4096)` 调用
- **THEN** 返回 4096 字节；无 panic / 无 OOM

### Requirement: NextInt / NextLong 是 GetBytes 的 typed wrapper

#### Scenario: NextInt 调用
- **WHEN** 100 次 `Std.Crypto.SecureRandom.NextInt()` 调用
- **THEN** 至少出现 1 个负值 + 1 个非负值（i32 全范围分布）

#### Scenario: NextLong 调用
- **WHEN** 100 次 `Std.Crypto.SecureRandom.NextLong()` 调用
- **THEN** 至少出现 1 个负值 + 1 个非负值（i64 全范围分布）

### Requirement: NextU32Bounded uniform distribution

#### Scenario: 上界 = 1
- **WHEN** `Std.Crypto.SecureRandom.NextU32Bounded(1)` 调用
- **THEN** 返回 0

#### Scenario: 上界 = 10
- **WHEN** 100 次 `Std.Crypto.SecureRandom.NextU32Bounded(10)` 调用
- **THEN** 全部值 ∈ [0, 10)

#### Scenario: 上界 = 8 均匀
- **WHEN** 1000 次 `NextU32Bounded(8)` 调用
- **THEN** 8 个 bucket 计数全部 ∈ [50, 250]（loose χ² bound around expected 125）

#### Scenario: 上界 ≤ 0
- **WHEN** `NextU32Bounded(0)` 或 `NextU32Bounded(-5)` 调用
- **THEN** throw `Std.ArgumentException`，message 含 "positive"

### Requirement: byte distribution sanity

#### Scenario: 1024 byte 不被 0 dominate
- **WHEN** `GetBytes(1024)` 返回值统计
- **THEN** 0 byte 出现次数 < 50（uniform u8 期望 ~4，threshold 留极大 margin）

## IR Mapping

无新 IR 指令；通过现有 `Builtin` 指令调用 `__crypto_random_bytes`。

## Pipeline Steps

受影响 pipeline：
- [ ] Lexer — 无
- [ ] Parser / AST — 无
- [ ] TypeChecker — 无（纯 stdlib z42 script + builtin）
- [ ] IR Codegen — 无
- [x] VM interp — `BUILTINS` 数组追加 `("__crypto_random_bytes", crypto::builtin_crypto_random_bytes)`
- [x] stdlib — `Std.Crypto.SecureRandom` 类与 4 个 method
