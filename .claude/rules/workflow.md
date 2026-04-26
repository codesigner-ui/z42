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
| **Spec**（`docs/design/` + `spec/`）| 人机合同，实现的唯一依据 |
| **Claude** | 自驱执行各阶段；不超 Scope；不猜测歧义 |

**User 介入点只有两个：** ① 规范审批（Proposal + Spec）；② 规范分歧裁决。其余全部 Claude 自驱。

---

## 目录结构

```
spec/
├── changes/                    ← 进行中的变更提案
│   └── <change-name>/          ← kebab-case，动词开头
│       ├── proposal.md         ← Why：动机与范围
│       ├── design.md           ← How：技术方案与决策
│       ├── specs/
│       │   └── <capability>/
│       │       └── spec.md     ← What：可验证的场景（本变更的 delta）
│       └── tasks.md            ← 实施清单（checkbox）
└── archive/                    ← 已归档变更（与 changes/ 并列）
    └── YYYY-MM-DD-<name>/
```

长期规范（新语法、IR 指令、VM 行为）最终同步到 `docs/design/`，不存在 `spec/` 中。

### 与 OpenSpec 原版的偏离（z42 本地约定）

本工作流脱胎于 [OpenSpec](https://openspec.dev/) 社区方法论，但有三处显式偏离：

| 维度 | OpenSpec 原版 | z42 本地 | 偏离理由 |
|------|-------------|--------|--------|
| **目录名** | `openspec/` | `spec/` | 去掉方法论品牌暗示，名字更中性 |
| **archive 位置** | `changes/archive/` 子目录 | `archive/` 与 `changes/` 并列 | archive 不是一个 change；并列使 "进行中 vs 历史" 语义清晰，扫描活跃变更不需排除子目录 |
| **顶层 specs 库** | `openspec/specs/<capability>/spec.md` 作为系统当前行为的 SoT | 无顶层 `spec/specs/`，长期规范改为 `docs/design/<feature>.md` | z42 的语言/IR/VM 规范用人类可读的叙事文档组织（给语言使用者读），而非结构化 capability spec；变更归档时人肉 merge 到 `docs/design/` |

这些偏离一经明确，不得在未经讨论的情况下回改 OpenSpec 原版结构。

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

**判断规则：类型优先，文件数作为 fix/refactor 内部细分。**
- `lang` / `ir` / `vm` 类型：**无论文件数量**，一律走完整流程（阶段 1–9）
- `fix` / `refactor` 类型：> 3 个文件 → 最小化模式（tasks.md）；1–3 个文件 → 直接实施；单行 bugfix → 直接修改

---

## 🔴 Spec-First Self-Check（lang / ir / vm 变更强制）

**触发条件**：任何变更涉及新语法、新 IR 指令、新 VM 行为、新关键字、新类型系统规则、新约束机制、新接口契约。

**在写第一行实现代码之前，Claude 必须通过以下 self-check**：

```
[ ] spec/changes/<name>/proposal.md 存在且 User 已确认
[ ] spec/changes/<name>/specs/<capability>/spec.md 存在且 User 已确认
[ ] spec/changes/<name>/design.md 存在且 User 已确认
[ ] spec/changes/<name>/tasks.md 存在
[ ] 阶段 6.5 实施前确认 gate 已通过（User 明确说"没问题 / 可以开始"）
```

**任一未达成 → 停，回到阶段 1–6 补齐，不得推进代码。**

**常见反例（皆为违规）**：
- ❌ 只有 `docs/design/<feature>.md`（长期规范）就开始写代码
  → 长期规范 ≠ `spec/changes/<name>/` 的 proposal/specs/design；两者都必须有
- ❌ "因为迭代中与 User 逐步沟通了方案，所以跳过 proposal/specs"
  → 对话中的确认不替代 spec；User 审批的是文档，不是聊天记录
- ❌ 只建 `tasks.md` 就开工（除非明确是 fix / refactor 类型）
  → lang/ir/vm 类型不能走最小化模式
- ❌ 写完代码后再补 proposal/specs
  → spec 的作用是在实施前定义边界，事后补齐只剩文档价值、失去约束价值

**如果拿不准变更类型**：优先按严格模式走（完整流程）。多写 3 个文档的代价远小于跳过 spec 造成的返工和边界漂移。

---

## 阶段 0：意图识别

**每次新对话首条消息触发：** Claude 自动读取 `.claude/projects/<project>/memory/MEMORY.md`
和当前阶段（roadmap + `spec/changes/` 进行中变更），主动汇报状态和下一步，再处理用户输入。

Claude 读到以下关键词时自动触发对应动作：

| 用户说 | Claude 做 |
|--------|-----------|
| "我想做 X" / "实现 Y" | 阶段 1（探索）开始 |
| "继续" / "下一步" | 状态恢复协议 |
| "直接做" / "快速开始" | 最小化模式：只写 tasks.md（**仅 fix/refactor 类型适用**） |
| "开始写代码" / "实施" | 阶段 7（须先通过阶段 6.5 确认）|
| "没问题" / "可以开始" | 阶段 6.5 通过 → 进入阶段 7 |
| "批量确认" / "一并授权" / "这一系列都按计划做" / "自动推进" | 阶段 6.5 批量授权模式启动 |
| "停" / "暂停" / "不要继续" | 终止批量授权，进入交互模式 |
| "验证一下" | 阶段 8 |
| "归档" / "完成了" | 阶段 9 |
| "分析一下" / "探索" | 阶段 1，不创建任何文件 |

**词汇警报（lang/ir/vm 关键词触发强制完整流程）**：
检测到 "新语法 / 新关键字 / 新 IR 指令 / 新约束 / 新接口契约 / 新类型规则 /
新 VM 行为" 等描述时，**不论用户用什么语气**（即便说"快速开始"/"直接做"），
都必须走阶段 1–9 完整流程，创建 proposal + specs + design + tasks，不得
降级到最小化模式。

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
spec/changes/<change-name>/
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

## Scope（允许改动的文件）

**必须列出每个具体文件路径**（不允许只写"模块名"或"目录名"）。每个文件必须能被 tasks.md 中至少一个 task 命中。

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/path/to/Foo.cs`     | NEW    | 新增文件 |
| `src/path/to/Bar.cs`     | MODIFY | 修改 X 字段 / Y 方法 |
| `src/path/to/Old.cs`     | DELETE | 删除（pre-1.0 直接删，不留兼容） |
| `docs/design/foo.md`     | MODIFY | 同步规范 |
| `tests/FooTests.cs`      | NEW    | 新测试 |

**只读引用**（理解上下文必须读，但不修改；不计入并行冲突）：

- `src/path/to/Existing.cs` — 用于理解 X 行为
- `docs/design/related.md` — 参考 Y 规则

**变更类型枚举**：`NEW` / `MODIFY` / `DELETE` / `RENAME`（rename 同时占用旧路径 DELETE + 新路径 NEW）。

**并行 spec 冲突检测规则**（用于判断多个 spec 是否能并行实施）：

| 情况 | 是否冲突 |
|------|--------|
| 同一文件同时出现在两个进行中 spec 的 Scope（任一非只读引用） | ✅ 冲突 → 必须串行 |
| 同一文件仅在一个 spec 的 Scope，另一个 spec 只是只读引用 | ❌ 不冲突 → 可并行 |
| 两个 spec 的 NEW 文件路径在同一具体子目录（如同时在 `src/Foo/` 下创建文件） | ⚠️ 视目录性质判断；通用容器（`src/`）不冲突，专属子模块（`src/Foo/Bar/`）按团队约定 |
| 两个 spec 都修改同一 markdown 文档的不同段 | ✅ 冲突 → 串行（避免 merge 冲突） |

> 拿不准时按冲突处理（串行实施），代价远低于事后 merge 冲突排查。

## Out of Scope
- [明确排除，防止范围蔓延]

## Open Questions
- [ ] [待确认问题]
```

**Scope 表必须满足的硬性约束：**

- 路径必须是项目内可解析的**具体路径**（不允许 `src/compiler/*` 这种通配，也不允许 `相关测试文件` 这种模糊描述）
- 每条 NEW / MODIFY / DELETE 必须能被 tasks.md 中**至少一项 task** 引用
- 反过来：tasks.md 中触及的所有文件必须**全部**列入 Scope；实施中临时发现需要改动的文件 → **立即停下回到阶段 3** 更新 Scope（违反 = 越界）

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
- [ ] 3.5 docs/design/ 文档同步（新语法 / IR / VM 行为）
- [ ] 3.6 docs/roadmap.md 进度表更新（若有特性完成某 pipeline 阶段）

## 备注
[实施中发现的问题 / 决策变更]
```

**任务粒度：** 每项对应一个明确的代码操作，30 分钟内可完成。每完成一项立即将 `[ ]` 改为 `[x]`。

---

## 阶段 6.5：实施前确认（Gate）

### 单 spec 模式（默认）

**规范文档（proposal / spec / design / tasks）全部就绪后，Claude 必须向 User 展示完整方案摘要并明确询问：**

```
## 实施前确认

以下规范已就绪，请确认是否有问题：

- **Proposal:** [一句话]
- **Spec:** [场景数量] 个验证场景
- **Design:** [关键决策摘要]
- **Tasks:** [N] 项任务

请确认是否有问题？有问题请指出，没问题我开始实施。
```

**规则：**
- User 明确说"没问题"、"可以"、"开始"等肯定回复后，才能进入阶段 7
- User 提出问题 → 修改对应文档 → 重新展示摘要 → 再次询问，**循环直到确认**
- **不得跳过此步骤**，即使 User 之前逐步确认了每个文档
- 最小化模式（fix / refactor）同样适用：展示 tasks.md 摘要 → 确认 → 实施

### 批量授权模式（Batch Approval）

**触发条件**：当一项规划被显式拆成多个 spec（如 C1 / C2 / C3 / C4），且 User 明确说"批量确认"、"一并授权"、"这一系列都按计划做"、"自动推进，不用每次问"等表述时启用。

**激活流程：**

1. Claude 一次性展示**所有受授权 spec 的摘要**（每个 spec 一段，含 proposal + spec scenarios 数 + design 关键决策 + tasks 项数）
2. User 明确说"全部开始 / 没问题 / 批量授权" → 所有受授权 spec 进入待实施队列
3. Claude 在内存中记录批量授权范围（spec 名单 + 已确认时间），后续切换 spec 时无需重新询问

**激活后 Claude 行为：**

- 按 spec 间依赖顺序逐个进入阶段 7 实施 → 阶段 8 验证 → 阶段 9 归档
- 每个 spec 归档后**自动开始下一个**，仅需汇报状态切换：

  ```
  ✅ C1 已归档（commit: <hash>），1/4 完成
  🟡 现在开始 C2: <name>
  ```

- 不再为每个 spec 单独执行阶段 6.5 的"展示摘要 + 询问"
- 不再在每次 commit / push 后等待 User 确认

### 批量授权的中断条件（必须停下询问）

任一事件发生 → Claude 立即停下，向 User 汇报，等待裁决，不得视为"已经全部授权"而自行决定：

1. **Scope 越界**：实施中发现需要修改授权 Scope 之外的文件 → 回阶段 3 更新当前 spec 的 Scope，或开新 spec
2. **测试失败超出当前 spec 范围**：发现 pre-existing failure 或外部回归
3. **规范冲突**：实施中发现两个 spec 的设计相互冲突，或与 docs/design/ 现有规范冲突
4. **决策点未覆盖**：spec 中未明确的设计点（如字段命名、错误信息措辞、性能权衡），不得自行决定
5. **依赖前置变更需调整**：例如 C1 落地后发现 C2 引用的 C1 字段需要重命名
6. **GREEN 失败**：当前 spec 验证未全绿（参见阶段 8）
7. **超出预期工作量**：某 spec 的实际任务量明显超出 tasks.md 估计（如 1.5x 以上）
8. **架构性发现**：实施中发现某个原本认为是局部的变更其实牵涉跨模块重构

### 批量授权下仍然必须的步骤

- 每个 spec 独立通过 GREEN 验证（阶段 8）才能进入下一个
- 每个 spec 单独 commit + push（不积压、不混合）
- 每个 spec 完成后立即归档到 `spec/archive/`
- `.claude/` 与 `spec/` 必须纳入提交
- 任何中断条件触发时立即停下

### 批量授权的边界声明

- **批量授权 ≠ 自动扩张授权**：授权范围**严格限于一开始展示并被 User 明确确认的 spec 名单**；新发现的需求必须重新走"提议 + 单 spec 确认"或"扩展批量名单 + 重新确认"
- 批量授权对"代码实施 + commit + push"生效；对**外部影响动作**（如 force-push、删除分支、改 CI 配置）仍需单独确认
- User 任何时候说"停"、"暂停"、"不要继续了" → 立即终止批量授权，进入交互模式

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

**实施过程中的验证要求：**

- 每个 refactor / fix / feature 实施后，立即在本地运行编译 + 测试
- 发现任何测试失败：
  - ✅ 若是当前变更导致 → 立即修复
  - ✅ 若是 pre-existing 失败 → 在本迭代修复，或明确说明原因后 User 确认
  - ⚠️ 若与当前 Scope 无关 → 记入 tasks.md 备注，新建独立 issue
- 不得跳过任何测试失败后继续实施下一个任务
- 整个阶段 7 完成后，进阶段 8 前必须全绿

---

## 阶段 8：验证（Verify）

**全绿（GREEN）标准：**

任何迭代进阶段 9 前，必须通过以下全部验证步骤，且 **所有测试全部通过**：

```bash
# 1. 编译验证（无编译错误）
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml

# 2. 编译器测试（必须 100% 通过）
dotnet test src/compiler/z42.Tests/z42.Tests.csproj

# 3. VM 测试（必须 100% 通过）
./scripts/test-vm.sh
```

**测试失败处理规则：**

| 情况 | 处理方式 |
|------|---------|
| **当前变更导致的新失败** | ❌ 必须在本迭代修复，不得 commit |
| **当前变更触发的隐藏 bug** | ❌ 必须修复，或明确说明理由后 User 确认 |
| **Pre-existing 失败（变更前已存在）** | ⚠️ 必须在 **同一迭代** 修复，或单独 issue 跟踪 |
| 发现的问题与当前 Scope 无关 | ✅ 记入 tasks.md，新建独立变更，不阻塞本迭代 |

**验证报告（Claude 必须输出）：**

```markdown
## 验证报告

### 编译状态
- ✅ dotnet build src/compiler/z42.slnx
- ✅ cargo build --manifest-path src/runtime/Cargo.toml

### 测试结果
- ✅ dotnet test: N/N 通过
  - 若有失败：❌ 失败列表与修复说明
- ✅ ./scripts/test-vm.sh: M/M 通过
  - 若有失败：❌ 失败列表与修复说明

### Spec 覆盖（若有 spec）
| Scenario | 实现位置 | 验证方式 | 状态 |
|----------|---------|---------|------|
| [场景名] | File.cs:行号 | [单元/golden/端到端] | ✅ |

### Tasks 完成度：N/N ✅

### 结论：✅ 全绿，可以归档 / ❌ 未全绿，待修复：[列出]
```

**全绿才能进阶段 9。** 任何未全绿的状态不得 commit / push。

---

## 阶段 9：归档（Archive）

1. 将 tasks.md 状态改为 `🟢 已完成`，更新日期
2. 移动目录：`spec/changes/<name>/` → `spec/archive/YYYY-MM-DD-<name>/`
3. **同步到长期规范**（所有变更类型均需执行，按下表）：

   | 变更类型 | 必须更新的文档 |
   |---------|--------------|
   | 新语法 / 语句 | `docs/design/language-overview.md` + `docs/design/<feature>.md` |
   | 新 IR 指令 | `docs/design/ir.md` |
   | 新 zbc / VM 行为 | `docs/design/<feature>.md` |
   | 新构建步骤 / CLI 参数 / 工程文件规则 | `docs/design/project.md` 或 `CLAUDE.md` |
   | 任意特性完成某个 pipeline 阶段（Parser / TypeCheck / IrGen / VM） | `docs/roadmap.md` Pipeline 实现进度表 |
   | fix / refactor（若涉及行为或机制变更） | 对应 `docs/design/` 文档必须更新 |
   | 新协作规则 / 工作流规则 | `.claude/rules/workflow.md` |
   | 语言设计决策变更（设计目标、phase 归属、设计理由） | `docs/features.md` |

   > **规则：任何改变了外部可见行为、机制、规则或约定的迭代，归档前必须有对应文档落地。**
   > 无文档 = 未完成，不得进入 commit 步骤。

4. **自动提交（无需 User 确认）**，包含以下所有相关文件：
   ```bash
   git add src/ docs/ examples/ \
           .claude/ spec/ \
           .gitignore *.md
   git commit -m "type(scope): 描述"
   git push origin main
   ```
   - `.claude/`（workflow、memory、规则变更）和 `spec/`（proposal、design、spec、tasks、archive）**必须纳入提交**，不得遗漏。
   - 每个逻辑单元单独提交，不积压。

---

## 会话恢复协议

User 说"继续"时，Claude 自动执行：

1. 扫描 `spec/changes/`（排除 `archive/`）
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
- 需要改动 Scope 外的文件（**即使在批量授权模式下也必须停**）
- 发现 Scope 外的 Bug → 记录到 tasks.md 备注，新建独立变更，不顺手修
- 规范未覆盖的接口 / 架构选择
- Done Condition 不明确
- 批量授权模式下任一中断条件触发（参见阶段 6.5"批量授权的中断条件"）

**Claude 自主决定（无需询问）：**
算法细节、数据结构选择、代码风格、变量命名。

**Scope 表的强约束：**
- proposal.md Scope 表是 Claude 在阶段 7 实施时**唯一允许触及的文件清单**
- 实施时发现需改动 Scope 外文件 → 立即停下，回到阶段 3 更新 Scope（哪怕只是一行小改），更新后必须 User 重新确认（批量授权下也是）
- "顺手改一下"、"反正在附近"、"应该没人在意" 都是违规，无例外

---

## 最小化模式（fix / refactor）

```
spec/changes/<name>/tasks.md   ← 必须有
```

tasks.md 顶部：

```markdown
# Tasks: <名称>
**变更说明：** [一句话]
**原因：** [一句话]
**文档影响：** [列出需要更新的文档，无则写"无"]

- [ ] 1.1 [任务]
- [ ] 1.x docs/design/ 或 workflow.md 更新（若有行为/机制变更）
```

完成后直接进阶段 8（验证）→ 阶段 9（归档）。

> **即使是 fix，只要改变了行为、机制或约定，就必须同步文档。** 只有纯内部实现调整（如重命名变量、提取方法、不改接口）才可跳过文档步骤。

---

## 实现方案原则（必须遵守）

**优先采用最终的、架构上最合理的方案，不得擅自选择临时方案（quick fix、hack、不完整实现）。**

- 如果存在"临时方案"与"最终方案"之分，默认实施最终方案
- 若当前条件确实迫使必须先采用临时方案（如依赖未就绪、工作量超出当前 Scope），**必须先明确说明原因，并得到 User 确认后方可实施**
- 不得以"先实现临时版本，以后再重构"为由跳过最终设计

### 修复必须从根因出发（2026-04-26 强化）

**修 Bug / 限制时，先识别根因（root cause），然后从源头修复，禁止在症状层面打补丁。**

典型反例（即"症状级临时方案"）：

- ❌ 在比较 / 转换层加同名兼容分支（`if (sourceName == targetName) return true`）来掩盖"类型对象本不应是不同种类"的设计错误
- ❌ 在调用方 try/catch 吞掉异常以"修好" 测试，而不去问 callee 为什么抛
- ❌ 用全局 if-else 特例分支处理某个具体类型，绕过通用机制
- ❌ 在解析失败时降级为 sentinel 值（`Z42PrimType("Unknown")` / 空字符串），让下游用启发式去"猜"

**根因修复检查清单**：

1. **数据为什么会到达错误状态？** —— 顺着调用栈反推产出该状态的代码点
2. **该代码点为什么写成这样？** —— 是不是因为依赖未就绪 / 信息不全 / 设计妥协？
3. **修复时改的是产出端还是消费端？** —— 必须改产出端（让数据从源头就正确），消费端的兼容分支只是症状级补丁
4. **修复后该错误状态是否物理上不可能再发生？** —— 是 → 根因修复；否（仍可能发生但被绕过） → 临时方案

**典型场景：跨阶段类型 / 元数据降级**

当某 Phase 1 因上下文不全把 X 降级为 Y（占位类型 / sentinel），Phase 2 信息齐了再用 X 时，正确做法是 **Phase 2 加一个 fixup pass 把 Y 升级回 X**（一次性，物理消除降级状态），而不是让所有消费端都加 `if (Y) treat-as-X` 的容错逻辑。

> 参见经典做法：C# / Java 编译器的"两阶段类型加载"（先建骨架 → 再填字段），消除 self-reference / forward-reference 时的降级。

### 系统性修复 vs Scope 控制

"系统性修复"不等于"Scope 蔓延"。判断标准：

- ✅ **属于本变更 Scope 内的根因**：修源头，禁止打补丁
- ⚠️ **根因在本变更 Scope 之外**：先停下报告 User，描述根因 + 请求 Scope 扩展（参见 W1 / VM-zpkg 案例的 Scope 扩展处理），User 批准后从根因修

**不允许"因为 Scope 限制就在症状层面修复"** —— 那只会在远端留下技术债。如果 Scope 真的不允许，留 backlog 给独立变更彻底修，本变更对该问题不动。

### 不为旧版本提供兼容（2026-04-26 强化）

**z42 当前处于快速迭代阶段（pre-1.0），不考虑旧版本兼容。** 任何格式 / API /
语义变更直接以最简形式 emit / 实施，**不留兼容路径**。

适用范围：

- **zbc / zpkg 二进制格式** — 字段添加 / 移除直接做；version bump 后旧版本
  zbc **不可读**（解码器无需 fallback）。残留旧产物用 `regen-golden-tests.sh`
  重生
- **stdlib 公开 API** — 接口 / 类 / 方法删除或重命名直接做；不留 alias / deprecation
- **IR 指令** — 新增字段 / 改语义直接做；旧字段不保留
- **TypeChecker / 编译器内部约定** — 同上
- **工程文件 / CLI 命令** — 同上

**禁止行为：**

- ❌ "保留旧字段以兼容已编译的 .zbc" — 用户重新 regen 即可
- ❌ "deprecation period 期内同时支持新旧 API" — 直接换
- ❌ "旧 minor version zbc 兼容路径" — minor / major 都不兼容旧版
- ❌ "灰度迁移，新旧并存" — 一次切干净

**唯一例外**：与外部世界的接口（操作系统 syscall / 文件格式如 UTF-8 等
通用规范）— 这些不是 z42 本身的"旧版本"。

**节奏说明**：到 z42 进入 1.0 / 自举完成阶段后，本规则会重写为"语义版本 +
deprecation 周期"。当前处于探索期，**任何兼容性投入都是过早优化**。

> 写代码 / spec 时主动检查：是否有"为兼容而保留"的逻辑分支？有 → 删除。

---

## 测试要求（必须遵守）

**每次新增需求或迭代（非纯文档/纯注释变更），必须包含对应的测试用例。无测试 = 未完成。**

| 变更类型 | 测试要求 |
|---------|---------|
| 新功能 / 新 pipeline 阶段 | 至少 1 个正常用例 + 1 个边界/异常用例的单元测试或 golden test |
| Bug fix | 至少 1 个回归测试，覆盖修复的 bug 场景 |
| 新 IR 指令 / VM 行为 | golden test (run/) 验证端到端执行结果 |
| 新 CLI 命令 / 工程文件字段 | 单元测试验证解析正确性 + 错误输入报错 |
| refactor | 确保已有测试仍覆盖（不新增测试即可，但不得删除测试） |

**测试位置：**
- C# 编译器：`src/compiler/z42.Tests/` 下对应测试类
- Rust VM：`src/runtime/tests/golden/run/` 或 `src/runtime/src/*_tests.rs`
- 跨语言端到端：golden test（source.z42 + expected_output.txt）

---

## 禁止行为

**必须遵守，违反即为严重工作流缺陷：**

- **Spec 未经 User 确认前写实现代码**
  - 规范驱动：所有非平凡变更必须先有 Spec（proposal + specs/<capability>/spec.md + design），User 批准后才开始代码
  - 参见本文件顶部 **🔴 Spec-First Self-Check** 小节 — lang/ir/vm 变更开工前逐项核对
  - **反例**（曾在 2026-04-24 静态抽象接口成员变更中发生）：只建 `tasks.md` +
    `docs/design/<feature>.md` 就开始实施，把聊天中的逐步确认当作 spec 审批；
    归档时被发现违规。纠正：`docs/design/` 长期规范与 `spec/changes/<name>/`
    变更规范是**两份独立文档**，lang/ir/vm 变更两者都必须存在

- **验证未全绿时 commit / push**
  - 🔴 **任何测试失败都不得进入 commit**
  - 包括 pre-existing 失败：发现后必须修复，或新建单独 issue + 说明
  - 验证命令必须完整运行：`dotnet build && cargo build && dotnet test && ./scripts/test-vm.sh`
  - 全绿的定义：所有编译无错，所有测试 100% 通过

- **顺手修复 Scope 外问题**
  - Scope 内改动优先完成 + 验证
  - 发现的外部问题记入 tasks.md 备注，新建独立变更，不阻塞本迭代

- **用"我理解你的意思是…"绕过歧义**
  - 存在歧义时停下来询问，而不是主观推断

- **interp 未全绿时填 JIT / AOT 实现**
  - VM 实现严格按顺序：interp ✅ → JIT → AOT

- **Phase 3 特性混入 Phase 1/2**
  - 检查 docs/features.md，确认当前 phase 限制

- **单次提交积压多个逻辑单元**
  - 每个 spec/changes/ 变更对应一个 commit
  - 不得在一个 commit 中混合多个独立的功能或修复

- **未经 User 确认擅自采用临时方案**
  - 存在"临时"与"最终"方案时，默认实施最终方案
  - 若条件不允许（依赖未就绪、工作量超出 Scope），必须先向 User 说明 + 得到确认

- **批量授权下扩张授权范围**
  - 批量授权严格限于 User 明确确认的 spec 名单
  - 实施中发现需要新增 spec、改动 Scope 外文件、调整既定决策 → 必须停下询问
  - "反正用户已经说全部授权了" 是错误理解；授权对象是**当时展示的内容**，不是"所有相关工作"

- **Scope 表使用模糊描述**
  - ❌ "src/compiler/* 相关测试"、"涉及的辅助函数"、"对应的文档"
  - ✅ 具体文件路径，每条对应至少一个 task；与 tasks.md 双向对齐
