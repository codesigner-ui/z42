# Spec: Remove Dead Value::Map

## REMOVED Requirements

### Requirement: alloc_map / Value::Map 不再存在

**Before**：
- `trait MagrGC` 提供 `alloc_map() -> Value`，返回 `Value::Map(Rc<RefCell<HashMap<String, Value>>>)`
- `Value::Map` 是 `Value` enum 的合法 variant
- interp / JIT / corelib 在 5 处 match arm 中处理 `Value::Map`
- `PartialEq for Value` 包含 `(Map(a), Map(b)) => Rc::ptr_eq(a, b)` 分支
- `value_to_str` 通过 `_ =>` 兜底处理 Map

**After**：
- `MagrGC` trait 上**不再有** `alloc_map()` 方法
- `Value` enum 上**不再有** `Map(Rc<RefCell<HashMap<String, Value>>>)` variant
- `Value::Map` 在源码任何位置都不再出现
- `PartialEq for Value` exhaustive 匹配剩余 7 个 variant，无 catch-all
- `value_to_str` exhaustive 匹配 7 个 variant（编译期强制覆盖）

## ADDED Requirements

### Requirement: Value enum 编译期 exhaustive 检查

#### Scenario: 加入新 Value variant 时所有 match 必须更新
- **WHEN** 未来在 `Value` enum 加入新 variant（如 `Value::Tuple`）
- **THEN** `value_to_str` / `PartialEq` 等无 catch-all 的 match 必须显式新增 arm，否则 cargo build 失败 —— 防止再次出现"variant 加进 Value 但消费侧忘记更新"的死代码

## IR Mapping

无 IR 变化（`Value::Map` 从未对应任何 IR 指令）。

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [x] VM interp
- [x] VM JIT helpers
- [x] corelib
