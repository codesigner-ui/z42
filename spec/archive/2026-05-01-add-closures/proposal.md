# Proposal: 添加闭包设计 (add-closures)

## Why

z42 当前只有顶层 `fn` 函数声明，无 lambda / closure。但：
- `concurrency.md` §6.3 已写"spawn 闭包 move 捕获"——闭包概念被引用却未定义，规范虚悬
- 即将推进的 stdlib（Map/Filter/Reduce）、L3 的事件/回调/async 全部依赖闭包
- L3 同时引入闭包 + Result + ADT + 泛型 + Lambda 时风险叠加，先把闭包设计独立锁定可降维

本变更**仅产出语言设计与规范文档，不动 src/**。具体实现拆为后续两个变更：
- `impl-lambda-l2`：无捕获 lambda + 表达式短写 + local function（L2 落地）
- `impl-closure-l3`：完整闭包（捕获 / 三档实现 / Send 派生 / 诊断选项）（L3 落地）

## What Changes

将对话讨论锁定的 15 条决议固化为正式规范：

1. 单一统一闭包类型，每签名一种
2. 隐式捕获，无 capture list
3. 值类型快照 / 引用类型按身份
4. 循环变量每次迭代新绑定（统一所有循环）
5. Lambda 语法 `=>`（C# 风格）
6. 单目标闭包，无多播；事件用 `EventEmitter<T>`
7. spawn 闭包强制 move + 自动 `Send`
8. 编译器自动选实现：栈 / 单态化 / 堆擦除三档
9. `--warn-closure-alloc` 编译选项（列出所有走档 C 的闭包字面量位置）
10. 共享可变值类型用 `Ref<T>` / `Box<T>`
11. 函数类型语法 `(T) -> R`
12. Local function（C# 7+ 嵌套函数声明）支持
13. 函数表达式短写 `R Name(T x) => expr;`（C# 7+ expression-bodied）
14. 闭包可比较；不可序列化
15. L2 = 无捕获 lambda + 短写 + local function；L3 = 完整闭包

文档落地：
- 新增 `docs/design/closure.md` 作为闭包专题
- 更新 `language-overview.md` / `grammar.peg` / `ir.md` / `concurrency.md` / `roadmap.md`

衍生 follow-up（不在本变更内）：
- VM 诊断需求（对象引用链 / captured env dump / allocation site 追踪）→ 记到 `docs/design/vm-architecture.md` 未来工作

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/design/closure.md` | NEW | 闭包专题设计（核心规范）|
| `docs/design/language-overview.md` | MODIFY | 增加闭包章节 + 函数类型语法 + L 阶段定位 |
| `docs/design/grammar.peg` | MODIFY | 增加 lambda / 函数类型 / 表达式短写 / 嵌套函数文法 |
| `docs/design/ir.md` | MODIFY | 闭包相关 IR 指令草案（mkclos / callclos / vtable_call）|
| `docs/design/concurrency.md` | MODIFY | §6.3 改为引用 closure.md 的捕获规则 |
| `docs/roadmap.md` | MODIFY | 闭包设计 milestone 标记完成 |
| `spec/changes/add-closures/proposal.md` | NEW | 本提案 |
| `spec/changes/add-closures/specs/closure/spec.md` | NEW | 行为规范 |
| `spec/changes/add-closures/design.md` | NEW | 实现设计 |
| `spec/changes/add-closures/tasks.md` | NEW | 任务清单 |

**只读引用**：
- `docs/design/iteration.md` — 确认与迭代器场景契合
- `docs/design/customization.md` — 确认 L3 lambda 特性占位
- `docs/design/philosophy.md` — 设计哲学对齐
- `.claude/projects/-Users-d-s-qiu-Documents-codesigner-ui-z42/memory/feedback_*.md` — 已存原则

## Out of Scope

- ❌ src/ 下任何代码变更（实现拆到后续变更）
- ❌ stdlib 中 Map / Filter / Reduce 等具体高阶 API 的设计
- ❌ `EventEmitter<T>` / `Ref<T>` / `Box<T>` 的类型定义（独立 stdlib 提案）
- ❌ 内存模型决议（RC vs GC）—— closure spec 标注"依赖内存模型，待回填"
- ❌ 借用检查器 / 所有权模型（闭包按值类型快照规则可独立成立）
- ❌ async/await 关键字本身（concurrency.md 已涵盖，本 spec 只对接捕获规则）

## Open Questions（归档时已决议）

- [x] **函数表达式短写歧义** → 已在 grammar.peg `function_decl` 加入 `function_body` 规则（block / `=> expr ;`），与下条声明用 `;` 分隔无歧义。z42 现有 `examples/generics.z42` 已验证此形式可用。
- [x] **L2 阶段"无捕获 lambda"边界** → 在 closure.md Open Questions 中明确："常量字面量（如 `x => 42`）不算捕获，编译期内联处理"。
- [x] **闭包类型 vs 函数指针类型** → z42 **不引入** C / Rust 风的独立函数指针类型；无捕获 lambda 在 IR 层降级为函数引用，但用户视角统一是 `(T) -> R`（已写入 closure.md Open Questions）。
- [x] **`[expect_no_alloc]` 属性** → 已删除（决议 #9 简化为只保留 `--warn-closure-alloc`）。档 C 触发条件天然不在热路径，硬断言收益不足。
