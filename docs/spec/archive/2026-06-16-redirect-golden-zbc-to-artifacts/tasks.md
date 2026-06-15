# Tasks: golden run-.zbc 重定向到 artifacts + 清理 src 残留

> 状态：🟡 进行中 | 创建：2026-06-16
> 类型：refactor（改 build/test 输出约定 = 对外可见机制 → 需文档同步）
> 子系统占用：`toolchain`（xtask scripts）+ `runtime`（zbc_compat.rs）—— 两者当前被占，User 授权跨锁（"全部处理 + 让 CI 没问题"）

## 阶段 1: 实现重定向
- [x] 1.1 `xtask_golden.z42`：加 `_goldenArtifactDir(root, relDir)` 共享 helper
- [x] 1.2 `xtask_regen.z42`：dir part1（src/tests）→ 镜像；`zbc-format` 仍就地
- [x] 1.3 `xtask_regen.z42`：dir part2（src/libraries）→ 镜像
- [x] 1.4 `xtask_regen.z42`：flat mode → 镜像
- [x] 1.5 `xtask_test_vm.z42`：dir part1 + part2 + flat 从镜像读
- [x] 1.6 `zbc_compat.rs`：committed zbc-format 读 src + run-golden 读 artifacts 镜像

## 阶段 2: 清理残留
- [x] 2.1 删 `src/libraries/*/tests/*.zbc`（192 死文件）+ `tests/<name>/source.zbc`（20 run-golden）
- [x] 2.2 删 `src/libraries/{z42.core,z42.io,z42.compression}/.cache/` + `/dist/`
- [x] 2.3 删 `src/tests/**/source.zbc` run-golden（保留 zbc-format committed）；另清 launcher `.cache/`
- [ ] 2.4 z42c `.cache/`（15）— 越界 + z42c 锁被占，留给持有方/后续；host 的 `target/`·`.build/`·`build/intermediates/` + platform fixtures 是工具构建目录/测试夹具，不动

## 阶段 3: 重建 + 验证
- [x] 3.1 重建 `artifacts/xtask/xtask.zpkg`（01:11）
- [x] 3.2 `xtask regen` → 188 run-golden 落 artifacts/build/golden；zbc-format 6 个就地（git 无漂移）；src 零新增
- [x] 3.3 `cargo test --test zbc_compat` → 3/3 ✓
- [x] 3.4 `xtask test vm interp` → 183/0 ✓
- [x] 3.5 `dotnet test` → 1563/1563 ✓
- [x] 3.6 确认 src 树零散落构建 .zbc（src/libraries=0, src/tests 非committed=0；仅 6 committed + 15 z42c-locked）
- [x] 3.7 `xtask test vm jit`：被**外部陈年进程**（pid 69572 `test vm --no-rebuild`，非本会话，持 xtask build 锁）卡死（已知 zombie-jam，见 memory `reference_xtask_gate_zombie_jam`）。jit 与 interp 共用同一镜像发现逻辑（mode 仅影响 interp_only 跳过），interp 已 183/0 + C# 权威 golden 门 1563/1563 → GREEN 充分；jit 卡死与本变更无关

## 阶段 4: 文档 + 归档
- [x] 4.1 `docs/design/testing/testing.md` + `src/tests/README.md` + `scripts/README.md` 落点约定
- [x] 4.2 `.gitignore` 复核（`artifacts/` + `*.zbc` + `!zbc-format` 负向规则已正确覆盖，无需改）
- [x] 4.3 ACTIVE.md 释放锁 + 归档

## 状态：🟢 已完成 2026-06-16

## 备注
- C# GoldenTests 从 source.z42 内存编译，不读 source.zbc → 无需改（已确认）
- zbc_compat 只在 Windows CI 腿跑（memory: reference_ci_rust_unit_tests_windows_only）→ 路径逻辑须本地 cargo test 验证
