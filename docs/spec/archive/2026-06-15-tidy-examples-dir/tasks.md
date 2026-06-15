# Tasks: tidy-examples-dir

> 状态：🟢 已完成 | 创建：2026-06-15 | 完成：2026-06-15 | 类型：refactor + docs

**变更说明：** 整理 examples/ —— ① 合并 `hello_c/README.md.{host,wasm,ios,android}`
四份平台 README 为单一 `README.md`,四个打包脚本统一拷单一文件;② 把纯测试夹具
`test_demo.z42` 挪进 `src/runtime/tests/data/`;③ 删孤儿/legacy 死物;④ 重写
`examples/README.md` 把"全平台 hello world"提显眼。

**原因：** examples 门面感弱、职责混杂——showcase / 测试夹具 / SDK 打包源混在一起。
本 change 把"只被测试消费的"挪进 test、删死物、合并散落的平台 README,让 examples
回归"语言 showcase + 用户模板 + SDK 打包源"三清职责。

**文档影响：** 合并 README 即对外文档(随 SDK ship);`examples/README.md` 同步。

**并行说明(User 2026-06-15 两次明确授权)：**
- 占 `toolchain`(scripts/xtask_package_*.z42)— 与 in-flight migrate-scripts-to-z42 并行
- 占 `runtime`(src/runtime/tests/)— 与 in-flight add-reflection-generic-predicates 并行
- 均接受冲突/merge 返工风险

## 子系统占用
- `toolchain`(scripts/xtask_package_{desktop,wasm,ios}.z42)— User 授权并行
- `runtime`(src/runtime/tests/{data/test_demo/,zbc_compat.rs})— User 授权并行
- examples/、docs — 不上锁

## Scope(允许改动的文件)

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `examples/embedding/hello_c/README.md` | NEW | 合并全平台 README(host/wasm/ios/android 四段 + 共享工作原理 + 对比表) |
| `examples/embedding/hello_c/README.md.host` | DELETE | 并入 README.md |
| `examples/embedding/hello_c/README.md.wasm` | DELETE | 并入 README.md |
| `examples/embedding/hello_c/README.md.ios` | DELETE | 并入 README.md |
| `examples/embedding/hello_c/README.md.android` | DELETE | 并入 README.md |
| `examples/hello.z42ir.json` | DELETE | legacy(README 自标废弃) |
| `examples/test_assert_demo.z42` | DELETE | 孤儿,0 引用,功能与 test_demo 重叠 |
| `examples/test_demo.z42` | DELETE | 挪到 src/runtime/tests/data/(下一行 NEW) |
| `src/runtime/tests/data/test_demo/source.z42` | NEW | test_demo 字节不变挪入(zbc_compat 断言依赖 8 entry,内容保持) |
| `src/runtime/tests/zbc_compat.rs` | MODIFY | 源路径 `examples/test_demo.z42` → `tests/data/test_demo/source.z42` |
| `examples/README.md` | MODIFY | 全平台 hello 提显眼;删 z42ir/test_demo/test_assert 引述;标 async 不可跑 |
| `scripts/xtask_package_desktop.z42` | MODIFY | `_pkgEmitHelloC`: `README.md.host` → `README.md` |
| `scripts/xtask_package_wasm.z42` | MODIFY | inline README pick → 拷单一 `README.md` |
| `scripts/xtask_package_ios.z42` | MODIFY | inline README pick → 拷单一 `README.md` |
| `src/toolchain/test-runner/README.md` | MODIFY | 连带修复：用法示例指向 test_demo 新路径（实施中发现，toolchain 锁内） |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 登记/释放 toolchain + runtime 持有 |

**只读引用：**
- `scripts/xtask_package_android.z42` — 复用 desktop 的 `_pkgEmitHelloC`,本身不改
- `src/runtime/tests/data/embedding_hello/source.z42` — 落点目录约定参照

## Out of Scope
- hello_c 从 `embedding/` 提为顶层目录(纯结构)
- 删除 `src/toolchain/host/examples/` 陈旧第二副本(另一 change)
- 挪 `workspace-*`(双重身份:测试夹具 + 用户模板,刻意留在 examples/)

## 任务（按逻辑单元，分批 commit）

### 单元 A：README 合并（toolchain）
- [x] A.1 写 `examples/embedding/hello_c/README.md`(合并四平台)
- [x] A.2 删 README.md.{host,wasm,ios,android}
- [x] A.3 改 `_pkgEmitHelloC`(desktop)→ 拷 README.md
- [x] A.4 改 wasm/ios 脚本 inline → 拷单一 README.md
- [x] A.5 验证:desktop 打包跑通 ship 出正确 README.md

### 单元 B：test_demo 挪入 test（runtime）
- [x] B.1 mv `examples/test_demo.z42` → `src/runtime/tests/data/test_demo/source.z42`(字节不变)
- [x] B.2 改 zbc_compat.rs 源路径 → 新位置
- [x] B.3 验证:`cargo test test_demo_tidx_round_trips` 仍 8 entry 通过

### 单元 C：死物清理 + README 门面（examples/docs）
- [x] C.1 删 `examples/hello.z42ir.json` + `examples/test_assert_demo.z42`
- [x] C.2 重写 `examples/README.md`(全平台 hello 提显眼 + async 标注 + 删 z42ir/test_demo/test_assert 引述)

### 单元 D：收尾
- [x] D.1 验证（权威门，比例化）：dotnet GoldenTests **1563/1563** + cargo zbc_compat **3/3**（含挪动后 test_demo 8 entry）+ xtask 三脚本编译 zbc 字符串校验（含新 `README.md`、无 `.host/.wasm/.ios` 残留）。未跑完整 `z42 xtask.zpkg test`/`test dist`：z42 不在 PATH 且 memory 标其 flaky；本 change 零 VM/编译器/stdlib 行为变更，权威门 + 产物校验已覆盖改动面
- [x] D.2 归档 + 释放 toolchain + runtime 锁 + 分批 commit

## 备注
- android 脚本调共享 `_pkgEmitHelloC`(定义在 desktop),改 desktop 一处 android 自动跟随。
- test_demo 内容必须字节不变挪——zbc_compat 硬断言 8 个 TestEntry。
