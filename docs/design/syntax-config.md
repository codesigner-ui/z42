# z42 语法配置规范

## 设计目标

z42 的解析器已采用 Pratt 表驱动（`ParseTable.cs`），运算符优先级和语句规则以数据形式存储。
本规范在此基础上引入**三层语法配置机制**，使语法可以从外部定制，无需修改编译器源码。

```
第 1 层  项目配置    z42.toml [syntax] 节 — 启用/禁用内置特性
第 2 层  源文件指令  using syntax "..."  — 文件级特性覆盖
第 3 层  用户定义    operator / keyword  — 声明全新的词法/文法规则
```

---

## 第 1 层：项目级语法配置（`z42.toml`）

在 `z42.toml` 的 `[syntax]` 节中可以开关编译器内置的语言特性：

```toml
[syntax]
# 关闭不需要的特性（默认全部 true）
exceptions      = false
pattern_match   = false
async_await     = false
null_coalesce   = true

# 设置默认绑定力偏移（高级，通常不需要）
# bp_offset = 0
```

可配置的特性名称与 `LanguageFeatures` 对应：

| 特性名            | 控制内容                          |
|-------------------|----------------------------------|
| `control_flow`    | `if/while/for/foreach`            |
| `exceptions`      | `try/catch/throw`                 |
| `pattern_match`   | `switch` 表达式和语句              |
| `null_coalesce`   | `??` 运算符                       |
| `async_await`     | `async/await`                     |
| `string_interp`   | `$"..."` 字符串插值                |
| `cast`            | `(Type)expr` 显式类型转换          |
| `lambda`          | `x => expr` 和 `(x, y) => expr`  |

---

## 第 2 层：文件级语法指令（`using syntax`）

单个源文件可以在文件顶部用 `using syntax` 覆盖项目配置：

```z42
using syntax exceptions = false;
using syntax null_coalesce = true;
```

- 指令必须出现在所有命名空间声明和实际代码之前
- 作用范围：当前编译单元
- 与项目配置取**并集**（文件可以额外开启，但无法突破 workspace 级禁用）

---

## 第 3 层：用户定义语法

### 3a. 自定义中缀运算符

```z42
// 声明一个新的中缀运算符，优先级与加法相同（bp=70）
operator infix "++" as "strcat" bp 70;

// 之后可以直接使用
var s = "hello" ++ " world";
```

语法：
```
operator infix <symbol> as <builtin-op | fn-name> bp <int>;
operator prefix <symbol> as <builtin-op | fn-name> bp <int>;
```

- `symbol`：由非字母数字字符组成的新 token（不能与现有 token 冲突）
- `as`：映射目标，可以是：
  - 已有运算符名称（`"+"`, `"-"` 等）→ 直接别名
  - 用户定义函数名 → `f(left, right)` 调用
- `bp`：绑定力，建议使用现有层级（10/20/30/40/50/60/70/80/90）

### 3b. 自定义关键字语句

```z42
// 声明 "unless" 作为 if (!cond) 的语法糖
keyword stmt "unless" (cond: Expr) (body: Block)
    = if (!cond) body;
```

语法：
```
keyword stmt <name> (<param>: <AstKind> ...) = <desugar-expr>;
keyword expr <name> (<param>: <AstKind> ...) = <desugar-expr>;
```

- `AstKind`：`Expr`、`Block`、`Type`、`Ident`
- `desugar-expr`：使用参数名组合的展开模板，在 Parse 阶段展开为标准 AST

### 3c. 关键字别名

```z42
// 把 "function" 作为 "void" 的别名（Python 风格爱好者）
keyword alias "function" = "void";
keyword alias "fn" = "void";
keyword alias "elsif" = "else if";
```

---

## 作用域与可见性

| 配置来源       | 作用范围          |
|---------------|-----------------|
| `z42.toml`    | 整个项目          |
| `using syntax`| 当前源文件         |
| `operator`    | 当前源文件及其依赖  |
| `keyword`     | 当前源文件         |

`operator` 和 `keyword` 声明可以放在独立文件中，通过 `using` 引入：

```z42
using "my_syntax.z42";   // 导入语法扩展
```

---

## IR 映射

用户定义语法在 **Parse 阶段展开**（不引入新 AST 节点、新 IR 指令）：

```
source.z42
  └─ 词法分析（含用户 token）
       └─ Parse 阶段展开（operator/keyword 脱糖）
            └─ 标准 AST（与无扩展时相同）
                 └─ TypeCheck → IrGen → IR（不感知语法扩展）
```

这保证了 IR 层的稳定性，语法扩展不影响后端。

---

## 实现路径

### Phase 1（当前阶段）— 第 1 层

- 读取 `z42.toml` 的 `[syntax]` 节，构造 `LanguageFeatures`
- 替换当前 `features.toml`（测试专用）为正式的 `[syntax]` 机制
- 影响文件：`z42.Driver`（读取配置）、`LanguageFeatures.cs`（扩展字段）

### Phase 1（当前阶段）— 第 2 层

- 在 Lexer/Parser 入口处理 `using syntax` 指令
- 修改 `ParserContext` 接受运行时 `LanguageFeatures`（已有该字段）

### Phase 2 — 第 3 层

- Lexer 支持运行时注册新 token pattern
- `ParseTable` 支持运行时插入 `ParseRule`
- 实现 `operator` / `keyword` / `keyword alias` 的解析和展开

---

## 约束与限制

1. **不允许**覆盖核心关键字（`if`、`while`、`class`、`return` 等）
2. **不允许**定义优先级为 0 的中缀运算符（会破坏解析循环）
3. 自定义 `symbol` 必须由 `!@#$%^&*|<>?~` 中的字符组成，且不能是现有 token 的前缀
4. `keyword stmt` 展开必须是确定性的（不允许引用外部状态）
5. 循环引用的语法扩展（A 展开引用 B，B 展开引用 A）在编译期报错

---

## 示例：数学 DSL

```z42
// math_syntax.z42
operator infix "^" as Math.Pow bp 85;   // 高于乘法

// main.z42
using "math_syntax.z42";

void Main() {
    var x = 2 ^ 10;   // 展开为 Math.Pow(2, 10) = 1024
    Assert.Equal(1024, x);
}
```

## 示例：Python 风格

```z42
// py_syntax.z42
keyword alias "def" = "void";
keyword alias "elif" = "else if";
keyword alias "True" = "true";
keyword alias "False" = "false";
keyword stmt "unless" (cond: Expr) (body: Block) = if (!cond) body;

// main.z42
using "py_syntax.z42";

def Abs(int n) {
    unless (n >= 0) {
        return -n;
    }
    return n;
}
```
