# z42 复合赋值运算符规范

## 设计参考

| 来源 | 借鉴点 |
|------|--------|
| **C#** | 完整运算符集；`+=` 对 string 做拼接（与 `+` 语义一致） |
| **Rust** | 运算符重载通过 trait（Phase 2）；Phase 1 只处理内置类型 |

---

## Phase 1 范围

仅支持以下 5 个算术复合赋值，位运算符 `&=`、`|=`、`^=`、`<<=`、`>>=` 留到 Phase 2。

| 运算符 | 语义 |
|--------|------|
| `+=`   | `x = x + rhs` |
| `-=`   | `x = x - rhs` |
| `*=`   | `x = x * rhs` |
| `/=`   | `x = x / rhs` |
| `%=`   | `x = x % rhs` |

---

## 语法

```csharp
x += 1;
arr[i] -= 2;
s += " world";     // string 拼接，等价于 s = s + " world"
```

### 左值（LValue）种类

| 左值 | 示例 | Phase |
|------|------|-------|
| 局部变量 | `x += 1` | Phase 1 ✅ |
| 数组元素 | `arr[i] += 1` | Phase 1 ✅（需数组支持） |
| 字段访问 | `obj.field += 1` | Phase 2 |

---

## 语义

**完全脱糖**：编译器在 AST 阶段将复合赋值展开，不新增 IR 指令。

```
x += rhs  →  x = x + rhs
```

脱糖规则：
- 左值只求值一次（对数组索引重要：`arr[f()] += 1` 中 `f()` 只调用一次）
- 类型规则与对应的二元运算符相同（`int += double` 检查 `int + double` 的类型）

### 数组元素的求值顺序

```csharp
arr[f()] += 1;
```

正确展开：
```
%idx = call @f()          // f() 只调用一次
%old = array_get %arr, %idx
%one = const.i32 1
%new = add %old, %one
       array_set %arr, %idx, %new
```

---

## TypeChecker

- 检查左值类型与右值类型经过运算后结果类型可赋值回左值
- `string += string`：合法（字符串拼接）
- `int += double`：非法（结果为 double，不能隐式窄化回 int）——与 C# 一致

---

## Lexer / Parser 扩展

新增 TokenKind（如果还没有）：
```
PlusEq   +=
MinusEq  -=
StarEq   *=
SlashEq  /=
PercentEq %=
```

Parser 在赋值优先级层识别这些 token，构造 `CompoundAssignStmt(op, lvalue, rhs)` AST 节点。

---

## 不在此规范范围内

- `&=`、`|=`、`^=`、`<<=`、`>>=`（Phase 2，位运算支持后一并引入）
- 运算符重载（Phase 2）
- `??=` null 合并赋值（Phase 2）
