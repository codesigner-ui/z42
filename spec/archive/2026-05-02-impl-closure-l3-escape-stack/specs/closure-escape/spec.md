# Spec: Closure Env Escape Analysis & Stack Allocation

## ADDED Requirements

### Requirement: Non-escaping closure allocates env on stack/arena

When a closure created by `MkClos` provably does not escape the current function frame, its env must be allocated in a frame-local store (stack array, arena, or equivalent) instead of the GC heap.

#### Scenario: 局部使用，立即调用
- **WHEN** `int n = 5; var add = (int x) => x + n; var r = add(3);`
- **THEN** `MkClos` 走 stack/arena 路径；env 不进入 GC root；函数返回时随 frame 释放

#### Scenario: 传给 no-escape 形参
- **WHEN** `Filter(list, x => x > n);` 且 Filter 形参类型推断 / 标注为 no-escape
- **THEN** 同上 stack alloc；`Filter` 内部访问 env 不晋升到堆

#### Scenario: 同函数内多次创建相同 closure
- **WHEN** `for (var i = 0; i < 10; i++) { var c = () => i; c(); }`
- **THEN** 每次循环 stack alloc；不累计 GC 压力

### Requirement: Escaping closure stays on heap

封闭逃逸的 closure 保持当前堆分配语义，行为不变。

#### Scenario: closure 作返回值
- **WHEN** `(int) -> int Make(int n) { return (int x) => x + n; }`
- **THEN** Codegen 把该 closure 标记为 escape，emit heap MkClos；返回值仍是 `Value::Closure`

#### Scenario: closure 写入字段
- **WHEN** `class Btn { (int) -> int handler; } b.handler = (int x) => x * 2;`
- **THEN** heap alloc

#### Scenario: closure 写入数组
- **WHEN** `var fs: ((int) -> int)[] = [(x) => x + 1, (x) => x + 2];`
- **THEN** 每个 closure 走 heap path

#### Scenario: closure 传给可能逃逸的 callee（保守判定）
- **WHEN** `RegisterCallback(() => { /*…*/ });` 但 RegisterCallback 未标 no-escape
- **THEN** **保守 fallback heap**（避免误判导致 use-after-free）

### Requirement: Stack-allocated env survives non-escaping calls

栈/arena 分配的 env 在 closure 被作为参数传给 no-escape callee 时必须保持有效（callee 在 caller frame lifetime 内）。

#### Scenario: callee 在 caller 帧内访问 env
- **WHEN** stack closure `c` 传给 `Filter(c)`，Filter 内 `c(item)` 调用
- **THEN** Filter 帧上访问 env 时数据有效（caller 帧未释放）

### Requirement: Escape analysis is conservative — false positives only

分析必须保守：**逃逸误判（heap 当成 stack）会导致 UB → 严禁**；**保守误判（stack 当成 heap）只是错失优化 → 接受**。

#### Scenario: 控制流复杂时 fallback heap
- **WHEN** closure 创建后经过 multi-branch 后被 return / 写入字段
- **THEN** 分析器无法 100% 证明不逃逸 → fallback heap，不冒险

## MODIFIED Requirements

### Requirement: MkClos IR shape

**Before**: `MkClosInstr(Dst, FnName, Captures)` —— 一律堆分配。

**After**: `MkClosInstr(Dst, FnName, Captures, bool StackAlloc = false)` —— 编译器在 escape 分析后填 StackAlloc；为 true 时 VM 走 frame-local 路径。

### Requirement: Closure value runtime representation

**Before**: `Value::Closure { env: GcRef<Vec<Value>>, fn_name: String }` —— env 总是 GC heap。

**After**: 由 design.md Decision 1 定型。三个候选方案 — User 裁决后此 spec 段会被收紧到具体形态。

## IR Mapping

复用 IR 指令；扩展 1 个字段：
- `MkClosInstr` —— 增加 `StackAlloc: bool`（zbc 编码加 1 字节 flag）
- `LoadFnInstr` —— 不变（不带 env，无逃逸概念）
- `CallIndirectInstr` —— 不变（运行时根据 closure value 自适配）

## Pipeline Steps

- [x] Lexer — 无影响
- [x] Parser / AST — 无影响
- [ ] TypeChecker / Bound — 增加 escape 分析 pass，写 BoundLambda.StackAllocEnv
- [ ] IR Codegen — MkClosInstr.StackAlloc 字段
- [ ] zbc 编码 / 解码 — 1 字节 flag
- [ ] VM interp — frame-local 路径 + arena 管理
- [ ] VM JIT — 镜像 interp 路径
