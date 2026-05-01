# Tasks: 添加闭包设计 (add-closures)

> 状态：🟢 已完成 | 创建：2026-05-01 | 归档：2026-05-01 | 类型：lang（design-only）

## 进度概览
- [x] 阶段 1: 核心规范文档（docs/design/closure.md）
- [x] 阶段 2: 现有文档同步（5 个文件 + customization.md 关键字表）
- [x] 阶段 3: 一致性验证 + 归档准备
- [x] 计划外修订：5 份文档 Rust 风语法 → C# 风（实施中发现冲突，user 选 Plan A）

## 阶段 1: 核心规范文档

- [x] 1.1 创建 `docs/design/closure.md`，包含：
  - § 概述："C# delegate 减去 MulticastDelegate"，单一统一类型
  - § 设计哲学：易用优先 + 去 C# 陷阱 + 性能可达 Rust
  - § 语法：lambda / `(T)->R` / 表达式短写 / 嵌套 `fn`
  - § 捕获语义：值类型快照 / 引用类型按身份 / 循环变量新绑定 / spawn move
  - § 单目标：闭包不支持多播；事件指向 `EventEmitter<T>`
  - § 实现策略：档 A 栈 / 档 B 单态化 / 档 C 堆擦除
  - § 性能诊断：`--warn-closure-alloc` 编译选项
  - § 共享可变值：`Ref<T>` / `Box<T>`
  - § 类型行为：可比较；不可序列化
  - § L 阶段：L2 = 无捕获 lambda + 短写 + 无捕获 local fn；L3 = 完整闭包
  - § 并发对接：spawn / async move + Send（引用 concurrency.md §6.3）
  - § 待回填：内存模型决议后补充档 C 编码、弱引用支持

- [x] 1.2 在 `docs/design/closure.md` 中加入跨文档链接：
  - 上游：`philosophy.md` / `language-overview.md`
  - 下游：`ir.md` / `grammar.peg` / `concurrency.md`
  - 相关：`iteration.md`（高阶 API 用例）/ `customization.md`（L3 占位）

## 阶段 2: 现有文档同步

- [x] 2.1 `docs/design/language-overview.md`：
  - 关键字表 `lambda` (L3) → `lambda` (L2 无捕获 / L3 完整)
  - 新增"闭包"小节，挂载语法概览
  - 函数类型 `(T) -> R` 加入类型系统章节
  - 表达式短写 `R Name(T x) => expr;`（C# 7+ expression-bodied）加入函数声明章节
  - Local function 加入函数声明章节

- [x] 2.2 `docs/design/grammar.peg`：
  - 新增 `LambdaExpr` 文法
  - 新增 `FnTypeExpr` 文法
  - 修改 `FnDecl` 文法，body 支持 `=> expr ;` 形式
  - 修改 `Block` 文法，允许 `FnDecl` 在 statement 位置
  - 验证 `=>` 双重含义无歧义

- [x] 2.3 `docs/design/ir.md`：
  - 新增"闭包指令"章节
  - 列出 `mkclos`（栈 / 堆变体）/ `callclos` 骨架
  - 列出 `mkref` / `loadref` / `storeref` 骨架
  - 标注：opcode 编号在 L3 实现时分配

- [x] 2.4 `docs/design/concurrency.md` §6.3：
  - 改为引用 closure.md R8
  - 保留 spawn / SpawnBlocking 自身调度语义
  - 检查 §6.4 / §6.5 等节对"闭包"的引用，统一指向 closure.md

- [x] 2.5 `docs/roadmap.md`：
  - 新增 milestone："L2 closure 设计完成 ✅"（本变更归档时打勾）
  - 新增 milestone："L2 lambda 实现"（→ `impl-lambda-l2` 后续变更）
  - 新增 milestone："L3 完整闭包实现"（→ `impl-closure-l3` 后续变更）

## 阶段 3: 一致性验证 + 归档准备

- [x] 3.1 文档交叉一致性检查：
  - 15 条决议在 `closure.md` / `proposal.md` / `spec.md` / `design.md` 表述一致
  - 术语（值类型 / 引用类型 / 单态化 / 类型擦除等）在既有设计文档中已定义或本文档自定义
  - `grammar.peg` 改动符合既有 PEG 风格

- [x] 3.2 与 memory 中 5 条 feedback 原则核对：
  - `feedback_unit_tests.md` ✅ design.md 含 Testing Strategy 矩阵
  - `feedback_generics_design.md` ✅ 档 B 单态化 + Fn bound 符合 Rust 风 + 大项目友好（dyn 路径）
  - `feedback_interop_pinned.md` ✅ 无捕获 fn ptr 可与 `[Extern]` 互通，捕获闭包不跨 FFI
  - `feedback_design_integrity.md` ✅ 设计冲突点已停下讨论 + 用户裁决（如 C# 兼容 vs 防陷阱）
  - `feedback_leak_via_diagnostics.md` ✅ 长寿 this 决议已删，转嫁 GC 诊断

- [x] 3.3 Open Questions 收尾（写入 closure.md 或显式留 placeholder）：
  - 函数表达式短写歧义 → grammar.peg 同步时实证
  - "无捕获 lambda" 边界 → 明确"常量字面量不算捕获"
  - 闭包类型 vs 函数指针类型 → 明确 z42 不引入独立 `fn(T) -> R` 类型

- [x] 3.4 follow-up 立项准备（仅记录到 roadmap，不在本变更内创建空目录）：
  - `spec/changes/impl-lambda-l2/`：L2 实现（无捕获 lambda + 短写 + local fn）
  - `spec/changes/impl-closure-l3/`：L3 实现（完整闭包 + 三档 + 诊断）
  - VM 诊断需求：引用链分析、captured env dump、allocation site tracking

- [x] 3.5 验证报告（本变更 design-only，无 build / test 运行）：
  - ✅ 所有 markdown 文档解析无错（手动检视）
  - ✅ 跨文档链接全部可达
  - ✅ 与 5 条 feedback memory 无冲突（含本对话新增的 leak_via_diagnostics）
  - ✅ 与现有 docs/design/ 无冲突（特别是 concurrency.md / iteration.md / customization.md）

## 备注

- 本变更为 lang 类型 **design-only**，不动 src/，不需要执行 `dotnet build` / `cargo build` / `dotnet test` / `./scripts/test-vm.sh`
- 测试用例的实际编写在后续 `impl-lambda-l2` 与 `impl-closure-l3` 落地
- 文档同步规则（workflow.md 阶段 9）：本变更触及"新语法 / 新 IR 指令草案"两类，必须更新 `language-overview.md` / `grammar.peg` / `ir.md`，已在阶段 2 中纳入
- 与 concurrency.md §6.3 的同步是必须的——归档前必须完成
