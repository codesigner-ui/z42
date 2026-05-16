# Tasks: extend `z42c run` to accept single-file `.z42` scripts

> 状态：🟢 已完成 | 创建：2026-05-17 | 完成：2026-05-17 | 类型：feat（CLI 扩展，无新 IR/VM）
> Spec 类型：minimal mode

## 背景

`z42c run` 当前只接受 `.z42.toml` manifest（编译 zpkg → exec z42vm）。但 script
自举（Phase 1 of shell → z42）下大量 `.z42` 是单文件无 manifest 的脚本
（如 `scripts/check-versions-drift.z42`），目前要走 4 步：

```bash
dotnet build z42c          # ensure compiler built
build-stdlib               # ensure stdlib zpkgs available
z42c file.z42 --emit zbc -o /tmp/x.zbc      # compile
z42vm /tmp/x.zbc Z42Foo.Main                # exec
```

本 spec 让 `z42c run scripts/check-versions-drift.z42` 直接做完后两步：
in-memory 编译 + 自动 Main 检测 + exec。bash bootstrap 因此瘦身 ~10 行。

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. CLI 兼容 | 新 subcommand `z42c run-script` / 复用现有 `z42c run` | 复用 | 用户视角统一：`z42c run X` 不管 X 是 manifest 还是 script |
| 2. 类型检测 | 文件扩展名 `.z42` vs `.toml` / 读文件内容判断 | 文件扩展名 | `.z42` 是 source，`.toml`/默认 manifest auto-discover 是 project；歧义少 |
| 3. 临时 zbc 位置 | mktemp / 内存 (in-memory exec) | mktemp | z42vm 需要 file path；in-memory 要进程内嵌 VM，scope 大 |
| 4. Entry 自动检测 | 复用 `AutoDetectEntry` | yes | 已有；同 PackageCompiler 行为一致 |
| 5. Entry 找不到 | 报错退出 | yes | 同 PackageCompiler 行为 |
| 6. argv 传递 | 直接转发到 z42vm | yes | `--` 后的参数透传；调用方 `z42c run script.z42 -- --verbose foo` 等价 `z42vm script.zbc Main --verbose foo` |
| 7. z42vm `--mode` | 转发 | yes | 与 project mode 一致 |
| 8. 临时 zbc cleanup | exit 后删除 | yes | trap-like cleanup（C# `using var tempDir`） |

## 阶段 1: 编译器实现

- [x] 1.1 MODIFY `src/compiler/z42.Driver/BuildCommand.cs::CreateRun`
  - argument 从 `ManifestArg()` 改成接受任意 path（保留 ZeroOrOne arity）
  - handler 内：if path.EndsWith(".z42") → `RunScript(path, mode, ...)`，else `RunProject(...)`
- [x] 1.2 NEW `BuildCommand.cs::RunScript(string scriptPath, string mode, ...)`
  - 复用 `SingleFileCompiler` 的 lex/parse/check 逻辑（refactor 或 inline）
  - 自动检测 Main entry（复用 `AutoDetectEntry` from `PackageCompiler.BuildTarget`）
  - 写 temp .zbc → exec z42vm path/to/zbc EntryName
  - cleanup temp dir

## 阶段 2: SingleFileCompiler refactor（如需）

- [x] 2.1 抽取 `SingleFileCompiler.Compile(...)` 返回 `(IrModule, Diagnostics)` —
  方便 `RunScript` 拿到 module 后做 entry 检测 + zbc 写入

## 阶段 3: bash bootstrap 简化

- [x] 3.1 MODIFY `scripts/check-versions-drift.sh` — 从 ~15 行降到 ~8 行
  （去掉手工 compile + mktemp，改成 `exec dotnet run ... -- run scripts/check-versions-drift.z42`）

## 阶段 4: 验证

- [x] 4.1 `dotnet run --project src/compiler/z42.Driver -c Release -- run scripts/check-versions-drift.z42` 输出 = bash 版输出
- [x] 4.2 故意 mismatch versions.toml → exit 1 + drift report
- [x] 4.3 `./scripts/check-versions-drift.sh` 端到端通过
- [x] 4.4 `dotnet test src/compiler/z42.Tests` 全绿
- [x] 4.5 `./scripts/test-stdlib.sh` 全绿

## 阶段 5: 归档

- [x] 5.1 mv → `docs/spec/archive/2026-05-17-add-z42c-run-script/`
- [x] 5.2 commit + push

## 实施期发现

1. **FindVm 走的是过时路径** `artifacts/z42/z42vm`（legacy layout）。当前
   dev 布局是 `artifacts/build/runtime/{release,debug}/z42vm`。趁机把 FindVm
   walk-up 顺序扩为：release → debug → legacy fallback。否则 z42c run 直接
   报"z42vm not found"。
2. **`dotnet run` 隐式 build 会重新打印 MSBuild warnings** 即使加了
   `--verbosity quiet`。bash bootstrap 加 `--no-build`（前一步 `dotnet build`
   已建过；run 阶段假定 binary 是新的）— stderr 终于干净。
3. **--bin 在 script mode 无意义** — 显式拒绝（exit 2 + 错误信息），避免用户
   误以为 script 也有 `[[exe]]` 概念。
