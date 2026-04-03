---
paths:
  - "**/*"
---

# z42 人机协作工作流（OpenSpec 风格）

> Claude 无需任何外部工具即可执行本流程。所有状态通过文件读写维护。

---

## 协作模型

```
Think First → Spec It → Build It → Archive It
  探索思考  →  写规范  →  写代码  →  归档留存
```

**角色分工：**

| 角色 | 职责 |
|------|------|
| **User** | 定方向、审批规范、裁决分歧 |
| **Spec**（`docs/design/` + `openspec/`）| 人机合同，实现的唯一依据 |
| **Claude** | 自驱执行各阶段；不超 Scope；不猜测歧义 |

**User 介入点只有两个：** ① 规范审批（Proposal + Spec）；② 规范分歧裁决。其余全部 Claude 自驱。

---

## 目录结构

```
openspec/
├── changes/
│   ├── <change-name>/          ← 进行中的变更（kebab-case）
│   │   ├── proposal.md         ← Why：动机与范围
│   │   ├── design.md           ← How：技术方案与决策
│   │   ├── specs/
│   │   │   └── <capability>/
│   │   │       └── spec.md     ← What：可验证的场景
│   │   └── tasks.md            ← 实施清单（checkbox）
│   └── archive/
│       └── YYYY-MM-DD-<name>/  ← 已归档变更
```

长期规范（新语法、IR 指令、VM 行为）最终同步到 `docs/design/`，不存在 `openspec/` 中。

---

## 变更分类

| 类型 | 触发条件 | 流程 |
|------|---------|------|
| `lang` | 新语法、关键字、类型规则 | 完整流程（阶段 1–9）|
| `ir` | 新 IR 指令、zbc 格式变更 | 完整流程 |
| `vm` | VM 执行语义变更 | 完整流程 |
| `fix` | Bug 修复，不改语义 | 最小化模式（仅 tasks.md）|
| `refactor` | 纯重构，不改行为 | 最小化模式 |
| `test/docs` | 测试或文档 | 直接实施，无需文件 |

**判断规则：** 改动 > 3 个文件 → 完整流程；1–3 个文件 → 最小化模式；单行 bugfix → 直接修改。

---

## 阶段 0：意图识别

Claude 读到以下关键词时自动触发对应动作：

| 用户说 | Claude 做 |
|--------|-----------|
| "我想做 X" / "实现 Y" | 阶段 1（探索）开始 |
| "继续" / "下一步" | 状态恢复协议 |
| "直接做" / "快速开始" | 最小化模式：只写 tasks.md |
| "开始写代码" / "实施" | 阶段 7（假设规范已确认）|
| "验证一下" | 阶段 8 |
| "归档" / "完成了" | 阶段 9 |
| "分析一下" / "探索" | 阶段 1，不创建任何文件 |

---

## 阶段 1：探索（Explore）

Claude 执行（不创建文件，仅输出）：

1. 读取相关源文件，理解现有结构
2. 梳理核心问题 / 需求
3. 列出潜在风险和边界情况
4. 提出 2–3 个可行方案（如有）
5. 给出推荐及理由
6. **等待 User 选择方案**，再进阶段 2

**z42 专项检查：**
- 确认属于哪个实现阶段（Phase 1/2/3），Phase 3 特性拒绝推进
- 确认影响的 pipeline 组件（Lexer / Parser / TypeChecker / Codegen / VM interp / JIT）

---

## 阶段 2：创建变更容器

```
openspec/changes/<change-name>/
├── proposal.md
├── design.md
├── specs/<capability>/spec.md
└── tasks.md
```

命名：动词开头，kebab-case，≤ 5 词。如 `add-for-loop`、`fix-type-check-crash`。

---

## 阶段 3：编写 proposal.md（Why）

草稿展示给 User → 确认后写入文件。

```markdown
# Proposal: <标题>

## Why
[1–3 句：背景和问题，不做会怎样]

## What Changes
- [变更点列表]

## Scope（允许改动的文件/模块）
| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|

## Out of Scope
- [明确排除，防止范围蔓延]

## Open Questions
- [ ] [待确认问题]
```

---

## 阶段 4：编写 specs/spec.md（What）

用可验证的场景定义行为。草稿展示 → User 确认 → 写入。

```markdown
# Spec: <Capability 名称>

## ADDED Requirements

### Requirement: <需求名>

#### Scenario: <正常场景>
- **WHEN** <触发条件>
- **THEN** <预期结果>

#### Scenario: <边界 / 异常场景>
- **WHEN** <条件>
- **THEN** <结果>

## MODIFIED Requirements
（修改现有行为时）
**Before:** <原行为>
**After:** <新行为>
```

**z42 专项（lang/ir 类型必须包含）：**

```markdown
## IR Mapping
[新语法对应的 IR 指令 / zbc opcode]

## Pipeline Steps
受影响的 pipeline 阶段（按顺序）：
- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [ ] VM interp
```

---

## 阶段 5：编写 design.md（How）

技术方案和决策记录。草稿展示 → User 确认 → 写入。

```markdown
# Design: <标题>

## Architecture
[ASCII 图或组件关系描述]

## Decisions

### Decision 1: <标题>
**问题：** ...
**选项：** A — 优/缺；B — 优/缺
**决定：** 选 X，因为 ...

## Implementation Notes
[关键 API、注意事项、边界条件]

## Testing Strategy
- 单元测试：[覆盖点]
- Golden test：[新增场景]
- VM 验证：dotnet test + ./scripts/test-vm.sh
```

---

## 阶段 6：编写 tasks.md（实施清单）

```markdown
# Tasks: <标题>

> 状态：🟡 进行中 | 创建：YYYY-MM-DD

## 进度概览
- [ ] 阶段 1: 基础
- [ ] 阶段 2: 核心实现
- [ ] 阶段 3: 测试与验证

## 阶段 1: 基础
- [ ] 1.1 [具体任务，指定文件和方法]

## 阶段 2: 核心实现（按 pipeline 顺序）
- [ ] 2.1 Lexer/Token（如有）
- [ ] 2.2 Parser/AST 节点（sealed record）
- [ ] 2.3 TypeChecker
- [ ] 2.4 IR Codegen
- [ ] 2.5 VM interp（interp 全绿前不碰 JIT）
- [ ] 2.6 单元测试 + golden test
- [ ] 2.7 examples/ 示例文件

## 阶段 3: 验证
- [ ] 3.1 dotnet build && cargo build —— 无编译错误
- [ ] 3.2 dotnet test —— 全绿
- [ ] 3.3 ./scripts/test-vm.sh —— 全绿
- [ ] 3.4 spec scenarios 逐条覆盖确认
- [ ] 3.5 docs/design/ 文档同步

## 备注
[实施中发现的问题 / 决策变更]
```

**任务粒度：** 每项对应一个明确的代码操作，30 分钟内可完成。每完成一项立即将 `[ ]` 改为 `[x]`。

---

## 阶段 7：实施（Apply）

对每个任务：
1. 宣告："正在处理 N.M: [描述]"
2. 读取相关文件
3. 实施代码变更
4. 将 tasks.md 中对应项 `[ ]` → `[x]`
5. 简短说明完成情况
6. 遇到阻塞 → 记录到 tasks.md 备注区 + 告知 User

**z42 pipeline 顺序（不跳步）：** Lexer → Parser → AST → TypeChecker → Codegen → VM interp → 测试

**规范偏差时：** 立刻停，不猜，不绕过。列出冲突 → User 裁决 → 更新 Spec → 继续。已验证部分不回头重写。

---

## 阶段 8：验证（Verify）

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
./scripts/test-vm.sh
```

**验证报告（Claude 输出）：**

```markdown
## 验证报告

### Spec 覆盖
| Scenario | 实现位置 | 状态 |
|----------|---------|------|
| [场景名] | File.cs:行号 | ✅ / ❌ |

### Tasks 完成度：N/N ✅

### 结论：✅ 可以归档 / ❌ 待修复：[列出]
```

**全绿才能进阶段 9。**

---

## 阶段 9：归档（Archive）

1. 将 tasks.md 状态改为 `🟢 已完成`，更新日期
2. 移动目录：`openspec/changes/<name>/` → `openspec/changes/archive/YYYY-MM-DD-<name>/`
3. **同步到长期规范**（lang/ir/vm 类型必须执行）：
   - 新语法 → `docs/design/language-overview.md`
   - 新 IR 指令 → `docs/design/ir.md`
   - 新 zbc 格式 → `docs/design/zbc.md`
4. 提交：`git add ... && git commit -m "type(scope): 描述" && git push origin main`

---

## 会话恢复协议

User 说"继续"时，Claude 自动执行：

1. 扫描 `openspec/changes/`（排除 `archive/`）
2. 读取进行中变更的 `tasks.md`，找到第一个未勾选任务
3. 读取 `CLAUDE.md` → 当前阶段约束
4. 读取 `memory/` → 跨会话决策
5. 汇报：

```
当前变更：<name>
已完成：X/N 项
下一步：任务 N.M — [描述]
继续？
```

6. User 确认后继续

---

## 越界防护

**必须停下询问（不得擅自决定）：**
- 需要改动 Scope 外的文件
- 发现 Scope 外的 Bug → 记录到 tasks.md 备注，新建独立变更，不顺手修
- 规范未覆盖的接口 / 架构选择
- Done Condition 不明确

**Claude 自主决定（无需询问）：**
算法细节、数据结构选择、代码风格、变量命名。

---

## 最小化模式（fix / refactor）

```
openspec/changes/<name>/tasks.md   ← 仅此一个文件
```

tasks.md 顶部：

```markdown
# Tasks: <名称>
**变更说明：** [一句话]
**原因：** [一句话]

- [ ] 1.1 [任务]
```

完成后直接进阶段 8（验证）→ 阶段 9（归档）。

---

## 禁止行为

- Spec 未经 User 确认前写实现代码
- 验证未全绿时 commit / push
- 顺手修复 Scope 外问题
- 用"我理解你的意思是…"绕过歧义，而不是停下确认
- interp 未全绿时填 JIT / AOT 实现
- Phase 3 特性混入 Phase 1/2
- 单次提交积压多个逻辑单元
