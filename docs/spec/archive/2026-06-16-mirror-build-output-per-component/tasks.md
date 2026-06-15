# Tasks: 构建产物按组件镜像 src 布局到 artifacts/build/

> 状态：🟡 进行中 | 创建：2026-06-16
> 类型：refactor（构建输出落点约定细化 = 对外可见机制 → 需文档同步）
> 子系统：`toolchain`（xtask golden/regen/test-vm/compiler-z42）+ `runtime`（zbc_compat.rs）+ `z42c`（16 测试 toml）
> 锁：toolchain 空闲取用；runtime（add-reflection-generic-type-definition 持有）+ z42c（port-z42c-self-compile 持有）User 跨锁授权 2026-06-16

## 背景
redirect-golden 把 golden run-.zbc 落到单独的 `artifacts/build/golden/<完整src路径>/`。User 要求改为**按组件镜像**（与 stdlib/z42c 包构建已有约定一致：`src/` 段剥离后挂 `artifacts/build/`）：
- `src/tests/<rel>` → `artifacts/build/tests/<rel>`
- `src/libraries/<lib>/tests/<rel>` → `artifacts/build/libraries/<lib>/tests/<rel>`
- `src/z42c/<member>/tests/<unit>` → `artifacts/build/z42c/<member>/tests/<unit>`（z42c 测试产物原落源码旁 .cache/dist）

## 阶段 1: golden 落点改镜像
- [x] 1.1 `xtask_golden.z42`：`_goldenArtifactDir` 改为 `artifacts/build/<relUnderSrc>`（relDir 去 src/ 前缀）
- [x] 1.2 `xtask_regen.z42` + `xtask_test_vm.z42`：调用处传 `tests/...` / `libraries/...`（去 src/）
- [x] 1.3 `zbc_compat.rs`：run-golden 改读 `artifacts/build/tests` + `artifacts/build/libraries`（递归 source.zbc）；committed zbc-format 仍读 src
- [x] 1.4 删旧 `artifacts/build/golden/`

## 阶段 2: z42c 测试产物重定向
- [x] 2.1 16 个 `src/z42c/<member>/tests/<unit>/*.z42.toml` 加 `[build] output_dir`（镜像到 artifacts）
- [x] 2.2 `xtask_compiler_z42.z42`：dist glob 改到 artifacts 镜像位置（与 toml 一致）
- [x] 2.3 删 src/z42c 下 15 个陈旧 `.cache/*.zbc` + 任何 `dist/`

## 阶段 3: 重建 + 验证
- [x] 3.1 重建 xtask.zpkg
- [x] 3.2 `regen` → golden 落 artifacts/build/tests + libraries；src 零新增
- [x] 3.3 `cargo test zbc_compat`
- [x] 3.4 `xtask test vm interp`
- [x] 3.5 `xtask test compiler-z42`（z42c 测试产物落 artifacts；src 零新增）
- [x] 3.6 src 树零散落构建 .zbc（除 6 committed zbc-format）

## 阶段 4: 文档 + 归档
- [x] 4.1 testing.md / src/tests/README.md / scripts/README.md 落点更新（golden/ → 组件镜像）
- [x] 4.2 ACTIVE.md 释放锁 + 归档

## 验证（隔离 worktree @ HEAD，规避并行 add-reflection WIP 的 jit/translate.rs 非编译态）
- regen 194 ok；golden→artifacts/build/tests(169)+artifacts/build/libraries/<lib>/tests(19)；src 零新增
- cargo zbc_compat 3/3；test vm interp 183/0
- test compiler-z42 exit 0；16 z42c 单元→artifacts/build/z42c/<member>/tests/<unit>/dist；src 零新增

## 状态：🟢 已完成 2026-06-16
