# Tasks: add-std-io-directory

> 状态：🟢 已完成 | 创建：2026-05-13 | 完成：2026-05-13 | 类型：fix/feature (stdlib API 扩展)

## 变更说明

`Std.IO` 新增 `Directory` 模块，补齐 `Create / Exists / Delete / Enumerate / EnumerateRecursive` 五个 API。

## 原因

stdlib 当前有 File / Path / Environment / Process，但**没有 Directory 模块**。这是 z42 build-driver backlog（[stdlib/roadmap.md "Deferred / Future Work"](../../../design/stdlib/roadmap.md#z42-build-driver-prerequisites2026-05-13)）阻塞清单 P0 项之一 —— 没有它 `.z42` 写的 build 脚本无法 `mkdir -p artifacts/...` 或扫 `src/tests/<cat>/<name>/`。

本次只做 Directory；其余 P0 缺口（File.Copy / Path.Separator 跨平台 / Environment.SetVar）独立迭代。

## 文档影响

- `docs/design/stdlib/roadmap.md` —— P0 z42.io.fs 段标注 Directory ✅
- `docs/design/stdlib/overview.md` Module Catalog 段添加 `Std.IO.Directory`（若 catalog 已枚举其他类）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.io/src/Directory.z42` | NEW | `public static class Directory` + 5 `extern` 方法 |
| `src/runtime/src/corelib/fs.rs` | MODIFY | 加 5 个 `builtin_dir_*` 函数 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 5 个 `__dir_*` builtin 进 table |
| `src/libraries/z42.io/tests/directory/source.z42` | NEW | golden test（mkdir / enumerate / cleanup） |
| `src/libraries/z42.io/tests/directory/expected_output.txt` | NEW | 期望输出 |
| `docs/design/stdlib/overview.md` | MODIFY | Module Catalog 加 `Std.IO.Directory`（仅当 catalog 列了其他类）|

**只读引用**：
- `src/libraries/z42.io/src/File.z42` — 参考 extern 声明 + 类型签名风格
- `src/runtime/src/corelib/fs.rs` — 参考 `builtin_file_*` 实现 + 错误处理风格

## Out of Scope

- `File.Copy` / `File.Move` —— 独立迭代
- `Path.Separator` 跨平台（Windows `\` 还是统一 `/`）—— 独立迭代，要先决策
- `Environment.SetEnvironmentVariable` —— 独立迭代
- 符号链接 / 权限 / 文件属性 —— 长期 backlog（z42.io.fs P0 扩展只做基础 API）

## API 设计

```z42
namespace Std.IO;

public static class Directory {
    // 存在性
    public static extern bool Exists(string path);

    // 创建。语义 = `mkdir -p`：递归建中间目录，目录已存在不报错。
    public static extern void Create(string path);

    // 删除。recursive=false 时仅删空目录；非空目录抛异常。
    public static extern void Delete(string path, bool recursive);

    // 列直接子项（含文件 + 子目录）。返回相对路径名（仅 basename，不含 path 前缀）。
    // 不存在时抛 Std.Exception。
    public static extern string[] Enumerate(string path);

    // 深度优先全展开。返回相对 path 的子路径（如 "sub/a.txt"）。
    public static extern string[] EnumerateRecursive(string path);
}
```

## 任务清单

- [x] 1.1 Spec 文件（本文件）
- [x] 2.1 `src/runtime/src/corelib/fs.rs`：加 5 个 `builtin_dir_*`（含递归 walk_dir helper）
- [x] 2.2 `src/runtime/src/corelib/mod.rs`：注册 5 个 `__dir_*` builtin
- [x] 3.1 `src/libraries/z42.io/src/Directory.z42`：新文件，5 个 `[Native]` extern 方法
- [x] 4.1 `src/libraries/z42.io/tests/directory.z42`：flat-mode 11 个 `[Test]`（Exists×3 / Create×3 / Delete×2 / Enumerate×2 / EnumerateRecursive×1）
- [x] 4.2 `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs`：z42.io 文件数 4 → 5（cached: 4/4 → 5/5）
- [x] 5.1 dotnet build / cargo build 通过
- [x] 5.2 `./scripts/test-all.sh` GREEN（dotnet 1233/1233 + test-vm 320/320 + test-cross-zpkg 1/1 + test-stdlib 7/7 file in 6 lib）
- [ ] 5.3 commit + push + archive 到 `docs/spec/archive/2026-05-13-add-std-io-directory/`

## 备注

### 实施 lesson

- 用户有并行 in-flight 工作（`add-std-process` + `add-host-package-conform`）触及同样的文件（`mod.rs` / `bench.rs` / `z42.io.z42.toml`）。本次实施期间 git stash + 选择性 staging 把双方隔离开，只 commit Directory 本变更
- 用户那两条 spec 的 commits 会单独由用户处理
- 实施期发现 2 个 pre-existing process 测试失败（`test_nonexistent_program_throws_start_exception` / `test_stdout_redirect_to_file`）—— 经 HEAD~3 复测验证是 pre-existing，跟本变更无关。但它们属于用户并行 in-flight 的 `add-std-process` spec 范围，会随那条 spec 解决，不在本 spec 处理

### 不踩的坑

- `Path.Separator` 仍硬编码 `'/'`（z42.io.Path 现状）—— 本变更不修；要做需先决定 Windows 上 `\` vs `/` 语义（独立 spec）
- `Directory.GetFiles` / `GetDirectories` 拆分 API（BCL 风格）—— 本变更只做 `Enumerate`（含两者）；分拆调用方自己 filter by `File.Exists` 即可

## 备注

- 跟 `builtin_file_*` 同款错误处理（`?` 让 IO 错冒成 anyhow / 抛 Std.Exception）
- enumerate 顺序不保证（OS 决定）；测试要排序后比对
