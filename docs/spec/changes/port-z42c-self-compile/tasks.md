# Tasks: port-z42c-self-compile

> 状态：🟡 进行中 | 创建：2026-06-15

**变更说明：** dogfood gap-batch——让自举 z42c 编译器能编译**自己的源码**（src/z42c/
7 包），逐个补齐 z42c 当前缺的语法/语义/stdlib 缺口（feedback_dogfood_fill_gaps：
缺口在 z42c 里实现，不绕过）。从最小叶子包 z42c.core 起，逐包推进。

**方法：** 用自举 driver（`z42c build <member-src-toml>`）编译各 z42c 包源 → 遇错即定位
缺口 → 在 z42c 实现 → 重建重试。最终目标：z42c byte-identical 编译全部 7 包 = 完整自举。

## 进度（按发现顺序）
- [x] G1 `_isVarDeclStart`：识别 `Name[] var`（用户类数组类型局部声明）——`int[]` 走类型关键字路径已 OK，用户类数组漏判。z42c.core/DiagnosticBag.z42 `Diagnostic[] bigger = new Diagnostic[n]` 触发。
- [ ] G2.. （继续编译 z42c.core 发现的后续缺口）

## 验证
- [ ] 每清一个缺口：`xtask test compiler-z42` 保持全绿（不回归 byte-compare 7/7 + zpkg 6/6）
- [ ] 里程碑：z42c 能编译 z42c.core 全部源（无 error）

## 备注
- 占用 z42c 锁（延续自举主线）。byte-identical 对账留各包能编译后再逐包验。
- workspace 约定默认 `[sources]`（成员无 [sources] 段时）= 单独的 driver/project 缺口，暂用显式 toml 绕过，后续补 workspace build 编排。
