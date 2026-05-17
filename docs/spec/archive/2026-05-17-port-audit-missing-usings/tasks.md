# Tasks: port audit-missing-usings.sh → audit-missing-usings.z42

> 状态：🟢 已完成 | 创建：2026-05-17 | 完成：2026-05-17 | 类型：feat（第二个 .z42 实现的 build script）
> Spec 类型：minimal mode

## 背景

Phase 1 of script self-hosting，第二个试点：100 LOC bash + `find` + `grep` +
`awk` + tempfile dance → 纯 z42 实现。

原 bash 职责：
1. 扫 `src/tests` + `src/libraries/*/tests` 下所有 `source.z42`
2. 按 type 使用模式（Console / File / Math / Random / StringBuilder / 等）
   推断需要的 `using <ns>;`
3. 已存在的 using 跳过；缺失的插入到 `namespace ...;` 之后（或文件顶端）
4. 报告 patched 数量

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. CLI 范围 | 仅 default 模式 / 含 per-file 模式 | 默认模式（无 args） | bash 版的 per-file args 主要 CI/maintainer 用；audit 是 idempotent，对所有文件跑一次成本可接受。Argv 透传到 z42c run 留 follow-up |
| 2. 文件枚举 | Directory.EnumerateRecursive + 过滤 / Path.Glob 多次 | Recursive + 过滤 | 两个根（src/tests + src/libraries）；每个 recursive enum 完后过滤 basename == "source.z42" |
| 3. Type 检测 | 7 个独立 Regex 编译一次复用 | yes | 每个文件读 1 次 + 7 次 IsMatch；相对成本低 |
| 4. 已有 using 检测 | Regex `^\s*using\s+Std\.X;` | yes | 同 bash grep |
| 5. 插入策略 | 找 namespace line / 文件顶端 | 同 bash | 用 String.Split('\n') + 行级处理 |
| 6. 写回 | File.WriteAllText 整文件 | yes | 不要 atomic rename 复杂度；audit 是开发工具，crash 安全度可接受 |
| 7. 输出报告 | 同 bash 格式 `patched: <file> (+<ns ns>)` | yes | byte-identical 期望 |
| 8. Exit code | 0 always（除非工具自身错误） | yes | 同 bash —— 这是 mutator，不是 lint |

## 阶段 1: z42 source

- [x] 1.1 NEW `scripts/audit-missing-usings.z42`
  - `namespace Z42AuditMissingUsingsScript;`
  - `void Main()` 入口：枚举所有 source.z42 → 对每个：detect needed → filter existing → insert
  - helpers: `_findSourceFiles(string root)` / `_detectNeeded(string content)` / `_existingUsings(string content)` / `_insertUsings(string content, string[] missing)`

## 阶段 2: bash bootstrap

- [x] 2.1 MODIFY `scripts/audit-missing-usings.sh`
  - 缩到 ~8 行：toolchain check + `exec dotnet run -- run scripts/audit-missing-usings.z42`

## 阶段 3: 验证

- [x] 3.1 `./scripts/audit-missing-usings.sh` 在干净 tree 上跑 → 0 patched（同 bash 版幂等性）
- [x] 3.2 故意删 1 个 `using Std.IO;` 然后跑 → 该文件被 patch 回
- [x] 3.3 stdlib regression 不回归

## 阶段 4: 归档

- [x] 4.1 mv → `docs/spec/archive/2026-05-17-port-audit-missing-usings/`
- [x] 4.2 commit + push

## 实施期发现

1. **z42.regex 不支持 `\b` 字边界**（escape `\b` 解析为字面 'b'）。bash 版用
   `\b(Console|...)\.` 严格匹配；z42 版改用 `(^|[^A-Za-z0-9_])(Console|...)\.`
   显式拒绝左侧 word char。罕见 false-positive（`MyConsole.X` 等）可接受 ——
   多加 using 不破坏编译。Backlog 候选：给 z42.regex 加 `\b` 支持（zero-width
   assertion，~30 LOC parser + engine 改）。
2. **`Directory.EnumerateRecursive` 返回相对路径**（不是全路径，per 现有 doc）。
   z42 版每次 `Path.Join(root, relPath)` 还原全路径以供后续 file ops。
3. **insert 算法是简单的"行级 split + rebuild"**：`String.Split("\n")` 丢了
   newline，rebuild 时按 `i < lines.Length - 1` 补回。同 bash awk 行为：
   尾行无 newline → split 出空字符串 element，rebuild 不补 \n → 文件末尾保持
   原状（无 / 有 trailing newline 两情况都正确）。
