# Spec: Std.Crypto.Random — CSPRNG

## ADDED Requirements

### Requirement: GetBytes returns OS-CSPRNG bytes

#### Scenario: 正常请求
- **WHEN** `Std.Crypto.Random.GetBytes(32)` 调用
- **THEN** 返回长度恰为 32 的 `byte[]`；每个 byte ∈ [0, 255]；连续两次调用结果不同（除非天文巧合）

#### Scenario: n = 0
- **WHEN** `Std.Crypto.Random.GetBytes(0)` 调用
- **THEN** 返回长度 0 的 `byte[]`，无 throw

#### Scenario: n 负数
- **WHEN** `Std.Crypto.Random.GetBytes(-1)` 调用
- **THEN** throw `Std.ArgumentException`，message 含 "n must be non-negative"

#### Scenario: 大 buffer
- **WHEN** `Std.Crypto.Random.GetBytes(65536)` 调用
- **THEN** 返回 65536 字节；不 panic / OOM；执行 < 100ms（loose bound）

### Requirement: NextInt / NextLong 是 GetBytes 的 typed wrapper

#### Scenario: NextInt 调用
- **WHEN** `Std.Crypto.Random.NextInt()` 调用
- **THEN** 返回 `int`（i32，可负）；连续两次结果几乎不可能相同

#### Scenario: NextLong 调用
- **WHEN** `Std.Crypto.Random.NextLong()` 调用
- **THEN** 返回 `long`（i64）

### Requirement: NextU32Bounded uniform distribution

#### Scenario: 上界 = 1
- **WHEN** `Std.Crypto.Random.NextU32Bounded(1)` 调用
- **THEN** 返回 0

#### Scenario: 上界 = 10
- **WHEN** 1000 次 `Std.Crypto.Random.NextU32Bounded(10)` 调用
- **THEN** 全部值 ∈ [0, 10)；每个 bucket 实际计数 ∈ [50, 200]（loose χ² bound around expected 100）

#### Scenario: 上界 ≤ 0
- **WHEN** `Std.Crypto.Random.NextU32Bounded(0)` 调用
- **THEN** throw `Std.ArgumentException`，message 含 "bound must be positive"

### Requirement: wasm32 unsupported

#### Scenario: wasm32 target
- **WHEN** wasm 构建里调用 `Std.Crypto.Random.GetBytes(16)`
- **THEN** throw `Std.NotSupportedException` with message 含 "CSPRNG not available on wasm32 — use add-csprng-wasm-bridge follow-up"

## IR Mapping

无新 IR 指令；通过现有 `Builtin` 指令调用 `__crypto_random_bytes`。

## Pipeline Steps

受影响 pipeline：
- [ ] Lexer — 无
- [ ] Parser / AST — 无
- [ ] TypeChecker — 无（纯 stdlib z42 script + builtin）
- [ ] IR Codegen — 无
- [x] VM interp — `BUILTINS` 数组追加 `("__crypto_random_bytes", crypto::builtin_crypto_random_bytes)`
- [x] stdlib — `Std.Crypto.Random` 类与 4 个 method
