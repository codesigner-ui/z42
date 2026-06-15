# Tasks: Type 泛型/基元谓词

> 状态：🟢 已完成 | 创建：2026-06-14 | 完成：2026-06-16 | 类型：vm（新反射行为/接口契约，无格式 bump，完整流程）

## 阶段 1: 反射 builtin
- [x] 1.1 `reflection.rs`：`is_primitive_type_name` 助手（keyword + Std.* BCL 名集合）
- [x] 1.2 `reflection.rs`：`builtin_type_is_generic`（type_params|type_args 非空）/ `builtin_type_is_primitive`（读 Name/__fullName 槽）
- [x] 1.3 `corelib/mod.rs`：注册 `__type_is_generic` / `__type_is_primitive`
- 收窄：`IsGenericTypeDefinition` 延后（实施期发现 typeof 不携带实例化 type args，开放/实例化不可区分）→ design.md Decision 2

## 阶段 2: stdlib
- [x] 2.1 `Type.z42`：`IsGenericType` / `IsPrimitive` extern 属性

## 阶段 3: 验证
- [x] 3.1 `cargo build` + `cargo test --lib` 全绿（release build OK；lib 808/0 + 21/0）
- [x] 3.2 golden `src/tests/types/generic_predicates.z42`（interp+jit；含于 xtask test vm 358/0）
- [x] 3.3 dotnet GoldenTests 全绿（253/0）+ 完整 dotnet test 1563/1563（无格式漂移）
- [x] 3.4 xtask vm 358/0（interp 183 + jit 175）/ cross-zpkg 2/0 / stdlib 272 file·22 lib
- [x] 3.5 docs reflection.md 同步 + spec scenarios 覆盖（主体节 + Deferred×2 + roadmap 索引×2）
- [x] 3.6 ACTIVE.md 释放锁 + 归档

## 验证备注（2026-06-16）
- 首次 `test vm` 报 45× `cannot read src/tests/types/*.zbc` = **陈旧 xtask.zpkg 假失败**（旧 test_vm 读就地 .zbc，当前源码已改读 artifacts 镜像 `artifacts/build/golden/`）。重建 xtask.zpkg（driver cached 27/27 = 已与当前源码一致）后重跑 `test vm --no-rebuild` → 358/0。非本变更回归（45 全是 missing-file，含 struct/record/typeof 等无关用例）。
- 加了 native builtin → `test lib` 自动重建 test-runner（内嵌 VM），272/22 绿。

## 备注
- 无格式 bump：纯运行期派生自 type_params/type_args + 类型名（GetGenericArguments 已用同源数据）。
- 无需 stdlib clean-rebuild / fixtures regen / embedded zbc regen（无 wire 改动）——比前两个反射增量轻得多。
- IsClass/IsInterface/IsEnum 延后（需类别元数据，format-bump）。
