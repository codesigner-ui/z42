# Tasks: cross-zpkg-e2e-test

> 状态：🟡 进行中 | 创建：2026-04-26 | 类型：test (新测试基础设施)

> **变更说明**：现有 `test-vm.sh` 只支持 single-source.z42 → source.zbc 模式，
> 无法验证多 zpkg 协作场景（如 L3-Impl2 跨包 impl 传播）。新增
> `tests/cross-zpkg/` 目录 + `scripts/test-cross-zpkg.sh` 驱动多 zpkg 端到端测试。
>
> **原因**：cross-zpkg-impl-propagation (commit 9284e30) 归档时的 backlog 项 ——
> 单元测试覆盖 IMPL section roundtrip + Phase 3 merge，但缺真·多 zpkg 用户代码
> 编译 + VM 运行的端到端验证。新机制 / 新扩展包出现时容易回归。
>
> **文档影响**：
>   - `src/runtime/tests/cross-zpkg/01_impl_propagation/` 第一个测试用例
>   - `scripts/test-cross-zpkg.sh` 测试驱动
>   - `docs/dev.md` 加测试命令说明
>   - `.gitignore` 排除测试 build 产物（dist/、artifacts/）

## Scope（具体文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/test-cross-zpkg.sh` | NEW | 多 zpkg e2e 测试驱动（构建 + 运行 + diff） |
| `src/runtime/tests/cross-zpkg/01_impl_propagation/target/z42.toml` | NEW | 目标 lib (Robot + IGreet) |
| `src/runtime/tests/cross-zpkg/01_impl_propagation/target/src/Robot.z42` | NEW | class Robot + interface IGreet |
| `src/runtime/tests/cross-zpkg/01_impl_propagation/ext/z42.toml` | NEW | 扩展 lib (depends on target) |
| `src/runtime/tests/cross-zpkg/01_impl_propagation/ext/src/Greeter.z42` | NEW | impl IGreet for Robot |
| `src/runtime/tests/cross-zpkg/01_impl_propagation/main/z42.toml` | NEW | 主 app (depends on target + ext) |
| `src/runtime/tests/cross-zpkg/01_impl_propagation/main/src/Main.z42` | NEW | 创建 Robot，调用 Hello() |
| `src/runtime/tests/cross-zpkg/01_impl_propagation/expected_output.txt` | NEW | 期望 stdout |
| `src/runtime/tests/cross-zpkg/README.md` | NEW | 测试目录约定 |
| `.gitignore` | MODIFY | 忽略 `tests/cross-zpkg/*/*/dist/` + `tests/cross-zpkg/*/*/artifacts/` |
| `docs/dev.md` | MODIFY | 加 test-cross-zpkg.sh 说明 |

**只读引用**：
- `scripts/test-vm.sh` — 风格参考
- `scripts/build-stdlib.sh` — 调用 z42c build 的方式参考
- `src/libraries/z42.io/z42.io.z42.toml` — 依赖声明格式参考

- [x] 1.1 写 `scripts/test-cross-zpkg.sh` — for each subdir under tests/cross-zpkg/，按 `target/ ext/ main/` 顺序 build，再运行 main 的 zpkg/zbc 比对
- [x] 1.2 创建第一个测试 `01_impl_propagation/`，覆盖 L3-Impl2 端到端路径
- [x] 1.3 README + dev.md 同步
- [x] 1.4 .gitignore 排除测试 build 产物
- [x] 1.5 验证：`./scripts/test-cross-zpkg.sh` 通过；不破现有 dotnet test / test-vm
- [x] 1.6 commit + push
