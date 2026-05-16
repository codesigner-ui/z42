# Tasks: port check-versions-drift.sh → check-versions-drift.z42

> 状态：🟢 已完成 | 创建：2026-05-16 | 完成：2026-05-17 | 类型：feat（首个 .z42 实现的 build script + z42.regex parser buffer-grow 修复）
> Spec 类型：minimal mode

## 背景

Phase 1 of script self-hosting：把最简单的 `scripts/check-versions-drift.sh`
（83 LOC bash + 依赖 `scripts/_lib/versions.sh` 的 python3 shellout）改为纯
z42 实现，证明 Phase 0 基础设施可用。

原 bash 脚本职责：
1. 读 `versions.toml`（SoT）
2. 对照若干"投影"文件（Cargo.toml / build.gradle.kts / package_helpers.sh）
3. 报告 drift；任一 mismatch → exit 1

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. Entry signature | `void Main` + Environment.Exit / `int Main` | `void Main` + Exit | 与现有 z42 程序一致；Exit 立刻终止有定义 |
| 2. TOML 读取 | shell out python / 直接 z42.toml | 直接 z42.toml | 整个目的就是去 python 依赖 |
| 3. 投影文件 line scanner | z42.regex / 手写 substring | z42.regex | regex 更可读；新落地的 z42.regex 也借此 dogfeed |
| 4. CWD 假定 | repo root 必须 / `git rev-parse` 自动找 | repo root 必须 | bash 版用 `$SCRIPT_DIR/..` 推；z42 版要求调用方 `cd $root` 后再跑（bash bootstrap 已 cd） |
| 5. 失败汇总 | 立即 exit / 全跑完再 exit | 全跑完再 exit | 同 bash 版语义；CI 一次看全部 drift |
| 6. Output format | bash printf %-50s 等价 | 简易左 pad helper | z42 无 printf；写 PadRight 即可 |
| 7. wasm 块 | 完全 port "presence check"（want == want 永远 pass） | 保留同 bash 行为 | 等同 schema 字段存在性 sanity |
| 8. bash bootstrap | 删 / 保留 stub | stub | self-host 边界：toolchain build 仍是 dotnet/cargo，无法用 z42 自启动 |

## 阶段 1: z42 source

- [x] 1.1 NEW `scripts/check-versions-drift.z42`
  - `namespace Z42CheckVersionsDriftScript;`
  - `void Main()` 入口：parse versions.toml + 对各文件 regex 抽值 + 汇总 + Environment.Exit
  - helper: `_padRight(string, int)` / `_tomlScalarToString(TomlValue)` / `_firstMatch(text, pattern, groupIdx)` / `_extractCargoWorkspaceVersion(text)`
  - 行为对齐 bash 版（同样的 ✓/✗ 输出、同样的退出码、同样的字段集）

## 阶段 2: bash bootstrap

- [x] 2.1 MODIFY `scripts/check-versions-drift.sh`
  - 内容从 83 LOC 缩到 ~15 LOC：cd repo root → build toolchain（dotnet + cargo + build-stdlib）→ compile script → exec z42vm

## 阶段 3: 验证

- [x] 3.1 `./scripts/check-versions-drift.sh` 输出 = 同 bash 版输出（同样字段、同样 ✓/✗ 计数、相同 exit code）
- [x] 3.2 故意把 versions.toml 某个字段改错 → 验证报错 ✓
- [x] 3.3 stdlib regression 不回归

## 阶段 4: 归档

- [x] 4.1 mv → `docs/spec/archive/2026-05-16-port-check-versions-drift/`
- [x] 4.2 commit + push

## 实施期发现

1. **z42.regex parser buffer-grow bug**（最关键）。`_parseConcat(buf, ...)`
   接 RegexNode[] 参数 + 内部 `buf = this._growNodes(buf, n)` reassign。
   z42 数组传递是 by-value-of-reference — callee 重新指向新 array，**caller
   局部变量仍指旧 array**。`^\s*version\s*=\s*"([^"]+)"`（14 AST nodes，超 8）
   parse 完后，`leftBuf` 还指 8-slot 旧 array，运行时 `seq[idx]` idx=8 越界。
   Fix: `_parseConcat` 改写 `this.ResultNodes` / `this.ResultCount`（class field），
   消除参数传递。`_parseAlt` 同步改 caller side 直接读 class field。
   所有 47 z42.regex tests + 60 全 stdlib tests 通过。
2. **bash 替换的最小套路**：`-q` 在 `dotnet run` 上模糊（被当作 program 参数），
   要写 `--verbosity quiet`。stdout / stderr 都要 `>/dev/null 2>&1` 才完全静默
   （z42c 把 "wrote → ..." 写到 stdout）。
3. **regex parser 这个 bug 没被 stdlib 测试覆盖到** — z42.regex 的 47 tests 用的
   pattern 都 ≤ 7 atoms (`^[A-Za-z0-9_]+$` 等)，永远没触发 grow path。Backlog 候选：
   加一个 large-pattern stress test。
