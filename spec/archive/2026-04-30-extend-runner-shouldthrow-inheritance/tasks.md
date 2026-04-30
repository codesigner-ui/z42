# Tasks: Inheritance-Aware ShouldThrow (A3)

> 状态：🟢 已完成 | 创建：2026-04-30 | 归档：2026-04-30
> 类型：refactor + small feature；最小化模式
> 依赖：A2（runner ShouldThrow runtime check ✅ 2026-04-30）

## 变更说明

`[ShouldThrow<Base>]` 现在也匹配 Base 的子类。**编译期展开方案**：C# IrGen 把 `E` + 所有从 `E` 派生（在当前 CU SemanticModel 可见范围内）的类短名拼成 `;`-delimited 字符串写入 TIDX；runner split 后任一命中即 Pass。

## 原因

A2 限制：`[ShouldThrow<Base>]` 必须**完全**匹配抛出类型；不会捕获 Base 的子类。

**实施过程发现的架构问题**：原计划走 runner-side `base_class` 链 walk（utilizing `LoadedArtifact.module.classes`），但 `--emit zbc` 单文件编译产物 `module.classes` 为空（dependencies 留给运行时 LazyLoader）。**改为编译期展开方案**——零 TIDX 格式 bump、零运行时类型反射、避免 runner 集成 LazyLoader 的范围扩张。

## 文档影响

- `docs/design/testing.md` R4.B "当前不做的"删 inheritance walk 一行；Runtime 比对子段加 inheritance 描述
- `docs/roadmap.md` M6 把"inheritance-aware spec 待开"标记为完成

## 实际交付（与原计划差异）

- ✅ C# IrGen `BuildShouldThrowChain(typeArg)` + `IsDescendantOf(cls, ancestorShortName)` helpers：拼 `typeArg` + 所有派生类短名为 `;`-delimited 字符串
- ✅ Runner `run_one` ShouldThrow 分支：split `expected` on `;`，对任一 candidate 调用现有 `type_matches`
- ✅ Rust 单元测试 4 个：单 entry / inheritance chain / no candidate / 空 segments 跳过
- ✅ C# 单元测试 +2：`IrGen_ShouldThrow_DescendantsIncluded` / `IrGen_ShouldThrow_TransitiveDescendantsIncluded`
- ✅ dogfood.z42 加 `test_assert_fail_caught_via_exception_base` 验证 `[ShouldThrow<Exception>]` 端到端
- ⏸️ **不做**：runner-side `type_matches_inherited(classes: &[ClassDesc])` —— 架构上不通（`--emit zbc` 单文件 `module.classes` 为空），改用上述编译期方案
- ⏸️ **不做**：跨非 import zpkg 依赖的 inheritance —— 留给 R3 完整版的 LazyLoader 集成

## 检查清单（实际执行）

- [x] 1.1 IrGen `BuildShouldThrowChain` + `IsDescendantOf` helpers
- [x] 1.2 IrGen `BuildTestEntry` ShouldThrow 分支调用 `BuildShouldThrowChain` 替代直接 Intern
- [x] 2.1 Runner `run_one` split `;`，对 candidates 任一 `type_matches` 命中即 Pass
- [x] 2.2 Rust 单元测试 4 个（list 行为）
- [x] 3.1 C# 单元测试 +2（DescendantsIncluded / TransitiveDescendantsIncluded）；现有 round-trip 用例改 chain assertion
- [x] 4.1 dogfood.z42 加 `[ShouldThrow<Exception>]` 测试（8/0）
- [x] 5.1 `cargo test runner` 17/17 ✅
- [x] 5.2 `dotnet test` 816/816 ✅
- [x] 5.3 `./scripts/test-vm.sh` 208/208 ✅
- [x] 5.4 `./scripts/test-stdlib.sh` 6/6 lib（dogfood 8/0）✅
- [x] 5.5 `./scripts/test-cross-zpkg.sh` 1/1 ✅
- [x] 6.1 docs/design/testing.md "Inheritance 比对（A3）" 段
- [x] 6.2 docs/roadmap.md A3 标记完成

## 备注

- 实施中发现 runner-side base-walk 方案在 `--emit zbc` 单文件场景下 `module.classes` 为空，及时 pivot 到编译期展开方案；过程留下 17 个 runner 单测 + 5 个 C# 单测 + 一段更准确的文档
- 编译期方案优势：零 TIDX 格式 bump、零运行时类型反射、零 runner 架构扩张
- 编译期方案局限：跨非 import zpkg 依赖的 inheritance 不覆盖（留给 R3 完整版）
