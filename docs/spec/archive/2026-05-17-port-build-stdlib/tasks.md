# Tasks: port build-stdlib.sh → build-stdlib.z42

> 状态：🟢 已完成 | 创建：2026-05-17 | 完成：2026-05-17 | 类型：feat（第三个 .z42 实现的 build script，dogfood Process.Run + File.Link）
> Spec 类型：minimal mode

## 背景

Phase 1 of script self-hosting，第三个试点。本脚本是 stdlib 构建主入口（被
test-stdlib / test-vm / package 间接依赖），也是首个 dogfeed
`Std.IO.Process.Run` + `Std.IO.File.Link` 的 z42 实现。

## 鸡生蛋问题

`build-stdlib.z42` 本身 `using Std.IO; using Std.Regex; using Std.Cli;` —— 编译
它需要 stdlib zpkgs 已存在。但本脚本的目的就是 **构建** stdlib。

**解决方案**：bash bootstrap 检测 `artifacts/build/libs/release/z42.core.zpkg`
是否存在；不存在则做 minimal primer build（一次 `z42c build --workspace --release`
+ 简易 cp -l flat view），然后才 dispatch 到 z42 script。常见情况（stdlib
已存在）primer 跳过，z42 script 直接接管 + 重建（incremental）。

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. CLI 参数 | `--debug` / `--use-dist` | 同 bash 版 | z42.cli 解析；保留 UX |
| 2. 编译器调用 | dotnet run / 包内 z42c | 同 bash 版（看 --use-dist） | Process.Run + WorkingDirectory + Stdio.Inherit() |
| 3. workspace 构建 stdout / stderr | Inherit | inherit | 用户能看到 z42c 编译进度 |
| 4. 失败处理 | exit 1 立即返回 | yes | 同 bash `exit 1` |
| 5. zpkg 列表 | 硬编码 16-name array | 同 bash | workspace 顺序敏感；硬编码 mirror `LIBS` |
| 6. Flat view 实现 | File.Link + fallback Copy | yes | 同 bash `cp -l ... || cp ...`；File.Link 来自 Phase 0b |
| 7. index.json 输出 | hand-build string vs z42.json.Stringify | hand-build | 与 bash heredoc 字节级一致 |
| 8. profile 传递给 z42 script | env var `Z42_BUILD_PROFILE` / argv | argv（标准 CLI） | z42 script 自己 parse |
| 9. primer trigger | 检测 `z42.core.zpkg` 缺失 | yes | 90% 情况跳过；first-clone 才跑 |

## 阶段 1: z42 source

- [x] 1.1 NEW `scripts/build-stdlib.z42`
  - parse CLI: `--debug` / `--use-dist`
  - 选择 compiler command
  - Process.Run workspace build（cd src/libraries + stdout/stderr inherit）
  - verify 16 zpkgs exist + size report
  - flat-view 清理 + hard-link
  - 写 index.json
  - print summary

## 阶段 2: bash bootstrap 重写

- [x] 2.1 MODIFY `scripts/build-stdlib.sh`
  - toolchain build (dotnet + cargo)
  - if stdlib 缺失：primer build + minimal flat view
  - dispatch to z42 script

## 阶段 3: 验证

- [x] 3.1 干净 clone 路径：删 `artifacts/build/{libraries,libs}` 后 `./scripts/build-stdlib.sh` 端到端成功
- [x] 3.2 增量路径：再次 `./scripts/build-stdlib.sh` cached
- [x] 3.3 `--debug` flag 正确切换 profile
- [x] 3.4 stdlib regression `./scripts/test-stdlib.sh` 不回归
- [x] 3.5 dotnet tests 不回归（incremental build test 依赖 cache 计数）

## 阶段 4: 归档

- [x] 4.1 mv → `docs/spec/archive/2026-05-17-port-build-stdlib/`
- [x] 4.2 commit + push

## 实施期发现

1. **`out` 是 z42 保留字第三次撞**（前 z42.uri / z42.regex / 这里）。每次重写
   为 `acc`。Backlog 候选：z42 让 `out` 只在 param modifier 上下文才保留，普通
   variable name 应允许（同 C# 实际行为）。
2. **z42 nullable type flow analysis 不工作**：`string? z42c = _findPackagedZ42c(); if (z42c == null) Exit(); compilerProgram = z42c;` 即使 null-check 后仍报
   `cannot assign string? to string`。Workaround：函数改返回 `""` 替代 `null`，
   消费侧 check `Length == 0`。同 z42.uri / z42.regex 处理 unmatched 结果的模式。
3. **chicken-and-egg primer 设计**：bash bootstrap 检测 `z42.core.zpkg` 是否
   存在；不存在 → minimal primer build + cp 拷 flat view → z42 script 自身重做
   "正确"的 build + flat-view + index.json。第二次以后 primer 块跳过，全程
   z42 主导。fresh clone 端到端 + warm cache 端到端都测过。
4. **Process API dogfood 验证 OK**：`Process(cmd).Args([...]).WorkingDirectory(...).Stdout(Stdio.Inherit()).Stderr(Stdio.Inherit()).Run()` builder pattern 用起来
   顺手；ProcessResult.ExitCode 透传给 Environment.Exit。Stdio.Inherit 让 z42c
   编译进度直接打到父 tty，无需 line buffer。
5. **File.Link 在跨 fs 时确实会失败**（design doc Deferred 写过）。bash 用
   `cp -l ... || cp` 兜底；z42 用 try/catch File.Link 失败回退 File.Copy。
   测试环境单一 fs，未触发 fallback，但代码路径备好。
6. **z42 stdlib 当前 ~16 lib 顺序敏感**（workspace topological build order +
   bash heredoc 字段顺序）。脚本里硬编码 16-name list；加新 lib 时需更新两处
   (`_stdlibList()` + `_indexJson()`)。Backlog 候选：从 workspace.toml
   default-members 读列表 + 自动 emit index.json（同 design doc Deferred
   "auto-derive from zpkg metadata"）。
