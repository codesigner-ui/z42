# Tasks: add-std-io-polish
**变更说明：** 补全 z42.io P0 缺口：File.Copy/Move、Environment.SetEnvironmentVariable、Path.Combine/IsRooted
**原因：** build-driver 阻塞清单需要这些原语；P0 z42.io.fs 路线图遗留项
**文档影响：** docs/design/stdlib/roadmap.md（build-driver 阻塞清单进度更新）

> 状态：🟢 已完成 | 创建：2026-05-12

- [x] 1.1 `fs.rs` — 添加 `builtin_file_copy`、`builtin_file_move`、`builtin_env_set`
- [x] 1.2 `mod.rs` — 在末尾追加 3 个新 builtin（`__file_copy`/`__file_move`/`__env_set`，保持现有 BuiltinId 不变）
- [x] 1.3 `File.z42` — 添加 `Copy(string src, string dst)` + `Move(string src, string dst)`
- [x] 1.4 `Environment.z42` — 添加 `SetEnvironmentVariable(string name, string value)`
- [x] 1.5 `Path.z42` — 添加 `Combine`（alias for `Join`）+ `IsRooted`（纯脚本）
- [x] 1.6 `src/libraries/z42.io/tests/file_extras.z42` — NEW：5 个测试（Copy × 3, Move × 2），全绿
- [x] 1.7 `src/libraries/z42.io/tests/env_set.z42` — NEW：3 个测试（SetVar 轮转 / 覆盖 / 空值），全绿
- [x] 2.1 测试验证（全绿）
  - dotnet build: ✅ 0 error
  - cargo build: ✅
  - dotnet test: ✅ 1248/1248
  - test-cross-zpkg: ✅ 1/1
  - test-stdlib: 新增 8 个测试全绿；7 个 process 文件失败均为 add-std-process 在途预存
  - test-vm: 43 个失败均为 HEAD 预存（与前后一致）
- [x] 3.1 docs/design/stdlib/roadmap.md — build-driver 阻塞清单 4/9 标完成
- [x] 3.2 commit + push

## 备注

- BuiltinId 重要约定：新 builtin 必须追加到 BUILTINS 末尾，不得插入中间（插入会移位现有 ID，破坏已编译 .zbc）
