# Spec: Catch Type Filter

## ADDED Requirements

### Requirement: Typed catch only matches declared type or its subclass

#### Scenario: Catch matches exact type
- **WHEN** `try { throw new IOException("x"); } catch (IOException e) { ... }` 执行
- **THEN** catch body 执行；e 绑定为该 IOException 实例

#### Scenario: Catch matches subclass via base type
- **WHEN** 自定义 `class FileNotFound : IOException` 抛出，且 catch 子句声明 `catch (IOException e)`
- **THEN** catch body 执行；e 是 FileNotFound 实例（动态类型保留）

#### Scenario: Catch does NOT match unrelated type
- **WHEN** `throw new IOException(...)` 但只有 `catch (NullReferenceException e) { ... }` 子句
- **THEN** 该 catch 不触发；异常向上传播（若无 finally + 无外层 handler，VM 走未捕获路径，exit code != 0）

#### Scenario: Multiple catches — first source-order match wins
- **WHEN** `try { throw new ArgumentException(...); } catch (IOException e) { A } catch (ArgumentException e) { B } catch (Exception e) { C }`
- **THEN** 仅 B 执行（C 是 base 但被 ArgumentException 拦截在前）；A 不执行

#### Scenario: Catch (Exception e) is universal exception catch
- **WHEN** 任何 throw value（z42 throw 必为 Exception 派生）后接 `catch (Exception e) { ... }`
- **THEN** catch body 执行；e 为 thrown 实例

### Requirement: Untyped catch retains wildcard semantics (backward compatibility)

#### Scenario: catch without type still catches all
- **WHEN** `try { throw ... } catch { ... }` 或 `catch (e) { ... }`（无类型声明）
- **THEN** catch body 执行；与未引入本变更前行为一致；现有 exception goldens 全部回归 PASS

### Requirement: Compile-time validation of catch type (E0420)

#### Scenario: Catch type must be known class
- **WHEN** `catch (NoSuchType e) { ... }` 中 NoSuchType 不存在于已加载符号表
- **THEN** 编译失败，报 `E0420 InvalidCatchType: catch type 'NoSuchType' not found`

#### Scenario: Catch type must be Exception subclass
- **WHEN** `class Foo {}` + `catch (Foo e) { ... }`（Foo 不是 Exception 派生）
- **THEN** 编译失败，报 `E0420 InvalidCatchType: catch type 'Foo' must derive from Exception`

#### Scenario: Catch (Exception e) is allowed
- **WHEN** `catch (Exception e) { ... }`（Exception 是 base）
- **THEN** 编译通过

### Requirement: Subclass match walks the BaseClass chain at runtime

#### Scenario: Multi-level inheritance match
- **WHEN** 自定义 `class L1 : Exception {}` `class L2 : L1 {}` `class L3 : L2 {}` 抛 L3，catch L1
- **THEN** L1 catch 触发（VM 沿 BaseClass 链上溯找到 L1）

#### Scenario: Mismatched derived branch doesn't match parent's sibling
- **WHEN** 抛 L3，catch 一个不在 L3→L2→L1→Exception 链上的类型 X
- **THEN** 该 catch 不触发

## MODIFIED Requirements

### Requirement: IrExceptionEntry.CatchType semantics

**Before:**
- 编译器对所有 catch 子句一律 emit `CatchType = null`（wildcard），完全不使用 AST `CatchClause.ExceptionType`
- `null` = 唯一的 catch 模式

**After:**
- `CatchType = null` — 用户写了 `catch { }` / `catch (e)`（无类型）→ wildcard，匹配任意 throw
- `CatchType = "FQ.ClassName"` — 用户写了 `catch (T e)` → VM 仅在 thrown.class instance-of T 时匹配
- `CatchType = "*"` — synthetic finally fallthrough catchall（已有；保持）
- 多 catch 同 try-region 时，按源序逐一比对，第一个匹配者胜

### Requirement: VM `find_handler` signature

**Before:**
```rust
fn find_handler(func: &Function, block_idx: usize, block_map: &HashMap<...>) -> Option<usize>
```
仅按 try-region 包含关系返回首个匹配 entry index，**不看 catch_type**。

**After:**
```rust
fn find_handler(
    func: &Function,
    block_idx: usize,
    block_map: &HashMap<...>,
    type_registry: &TypeRegistry,
    thrown: &Value,
) -> Option<usize>
```
按 try-region 包含 + catch_type 兼容性两个条件 AND 过滤；遍历顺序保持源序（exception_table 写入顺序即源序）。
- `catch_type = None` → 通过（wildcard）
- `catch_type = Some("*")` → 通过（synthetic finally catchall）
- `catch_type = Some(T)` → `is_subclass_or_eq_td(thrown.class, T)`

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：
- [x] Lexer — 无变化（catch / type 词法已有）
- [x] Parser / AST — 无变化（CatchClause.ExceptionType 已捕获）
- [ ] TypeChecker — 解析 + 校验 catch type，写入 BoundCatchClause；E0420
- [ ] IR Codegen — emit IrExceptionEntry.CatchType 用 BoundCatchClause.ExceptionTypeName
- [ ] VM interp — `find_handler` 加 catch_type filter
- [ ] VM JIT — `translate_throw` 路径同步 catch_type filter
