# Tasks: vm-wrapping-int-arith

> 状态：🟢 已完成 | 类型：vm (执行语义) | 创建：2026-04-28 | 完成：2026-04-28

**变更说明：** VM 整数算术（Add / Sub / Mul）改为 wrapping 而非 panic-on-overflow。对齐 C# / Java / 大多数 production VM 的行为，并解锁 hash 函数 / PRNG / 校验和等需要"模 2^64 算术"的算法。

**根因：** [interp/exec_instr.rs:84-92](src/runtime/src/interp/exec_instr.rs#L84) 用 Rust naked `x + y` / `x - y` / `x * y`，debug build panic on overflow。release build wrap（Rust 默认）—— 行为不一致。

**触发场景：**
- Std.Math.Random 当前用 Park-Miller LCG 是为了避开 multiply-overflow，但 BCL `Random` / Rust `rand` 都用 xorshift / xoshiro / PCG，需要 64-bit wrap 算术
- 任何 hash 函数（FNV / Murmur）需要 wrap
- 检验和（CRC、Adler）需要 wrap

**修复：** Add/Sub/Mul 用 `wrapping_add` / `wrapping_sub` / `wrapping_mul`。Div / Rem 保留 panic on /0（不同语义，不在本次范围）。Float 不变（NaN/Inf 已有定义）。

## Tasks

- [x] 1.1 `interp/exec_instr.rs`：Add/Sub/Mul 改 wrapping
- [x] 1.2 检查 JIT 路径已 wrap（Cranelift native code），无需改动
- [x] 2.1 升级 `Std.Math.Random` 用 xorshift64*（带 multiply）替代 Park-Miller，更好的统计性
- [x] 2.2 更新 `19_random` golden test 锁定新序列
- [x] 3.1 build-stdlib + regen + dotnet test + test-vm 全绿
- [x] 4.1 commit + push + 归档

## 备注

- 与 C# / Java 的 unchecked int 行为一致；对齐 Rust release build
- 不引入 `checked` 算术（z42 暂无该语法）—— 用户需要 overflow 检查时自行用 if 比较
- Float 不变：NaN / Inf 已有定义，无需 wrap
- **JIT 也需要修**：实施时发现 [helpers_arith.rs jit_add/sub/mul](src/runtime/src/jit/helpers_arith.rs) 的 fast-path 用 naked `x + y`，与 interp 改动一并修复。否则 JIT mode 会 panic on overflow 而 interp mode 正常 → 不一致
