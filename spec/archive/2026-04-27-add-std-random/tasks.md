# Tasks: add-std-random

> 状态：🟢 已完成 | 类型：feat (stdlib API) | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** 给 `z42.math` 添加 `Std.Math.Random` 类（伪随机数生成器），对标 BCL `System.Random` / Rust `rand`。纯脚本，无新 builtin。

**算法选择：Numerical Recipes LCG**（`state = state * 1103515245 + 12345`）。
- 优点：纯算术，无 bitwise 依赖；32-bit state 即够；快
- 缺点：低位质量不高、周期短（2^32）。够 demo / 简单游戏 / stdlib 自身测试用
- 严肃统计 / 加密用途待 L3+ 引入更好的 PRNG（PCG / Xoshiro / ChaCha）

**API：**

```z42
public class Random {
    public Random(int seed);
    public int Next();              // [0, int.MaxValue)
    public int Next(int max);       // [0, max)
    public int Next(int min, int max); // [min, max)
    public double NextDouble();     // [0.0, 1.0)
}
```

不提供无参构造（避免 z42.math 引入 z42.io 依赖只为读时间种子）。用户可手动 `new Random(Std.IO.Environment.GetCurrentTimeMs() as int)`。

## Tasks

- [x] 1.1 新建 `src/libraries/z42.math/src/Random.z42`：4 方法 + 构造
- [x] 2.1 新增 golden test `src/runtime/tests/golden/run/19_random/`：演示并锁定行为（固定 seed → 可重现序列）
- [x] 3.1 build-stdlib + regen + dotnet test + test-vm 全绿
- [x] 4.1 commit + push + 归档

## 备注

- 不引入新 builtin（纯脚本算术）
- 不依赖 z42.io（保持 z42.math 自给）
- 边界处理：state == int.MinValue 时跳过 -state 负溢出，return 0
- **实施中改算法**：原计划 Numerical Recipes LCG (i32 multiply with wrap)，实测 z42 VM 不支持 i32 wrap-around 算术（debug build panic on overflow），改用 Park-Miller (a=48271, m=2^31-1) 的 long 实现。同时因 z42 暂无 long→int narrowing cast，所有 Random 方法返回 long
- **算法**：Park-Miller `state = (state * 48271) % 2147483647`，state 必须 ≠ 0（构造时自动归一）
