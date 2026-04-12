# z42 Roadmap

## 固定决策

- **GC**：z42 始终带 GC，不引入所有权/借用（降低上手成本）
- **IR**：寄存器 SSA 形式
- **执行模式注解**：作用于命名空间级
- **`.z42bc` magic**：`Z42\0`

---

## 阶段总览

| 阶段 | 目标 | 状态 |
|------|------|------|
| **L1** | C# 基础子集，跑通完整 pipeline（源码 → IR → VM 执行） | ✅ 已完成 |
| **L2** | 基础设施完善（编译、工程、测试、VM 质量、标准库） | 🚧 进行中 |
| **L3** | 高级语法扩展（泛型、Lambda、异步 + z42 特有特性） | 📋 待开始 |

> 阶段严格串行：L1 pipeline 全通 → 启动 L2；L2 全完成 → 启动 L3。

---

## L1 — Bootstrap（C# 基础子集）

**目标**：以最小特性集跑通完整 pipeline：词法 → 语法 → 类型检查 → IR Codegen → VM 执行。

### 语言特性范围

| 类别 | 特性 |
|------|------|
| 基本类型 | `int`/`long`/`double`/`float`/`bool`/`char`/`string`/`void` + C# 数值别名（sbyte/ushort/uint…） |
| 运算符 | 算术、比较、逻辑、位运算、复合赋值、三目 `?:`、空合并 `??` |
| 控制流 | `if`/`else`、`while`、`do-while`、`for`、`foreach`、`switch` 表达式/语句、`break`/`continue`/`return` |
| 函数 | 顶层函数、方法、表达式体（`=>`）、默认参数值 |
| 类型定义 | `class`（字段、构造器、方法、属性、`static` 成员）、`struct`、`record`、`enum` |
| 可空类型 | `T?`、`?.` 空条件访问、`??` 空合并 |
| 集合 | `T[]` 数组、`List<T>`、`Dictionary<K,V>`（pseudo-class 策略） |
| 字符串 | 插值 `$"..."`、常用方法（Length/Split/Contains/ToUpper 等） |
| 异常 | `try`/`catch`/`finally`/`throw`、自定义异常类 |
| 内置 | `Console`、`Math`、`Assert`（pseudo-class） |
| z42 扩展 | `[ExecMode]` 执行模式注解、`[HotReload]` 热更新注解（命名空间级） |

### Pipeline 实现进度

| 特性 | Parser | TypeCheck | IrGen | VM | 备注 |
|------|:------:|:---------:|:-----:|:--:|------|
| 基本类型、运算符 | ✅ | ✅ | ✅ | ✅ | |
| `if` / `while` / `for` / `foreach` | ✅ | ✅ | ✅ | ✅ | |
| `do-while` | ✅ | ✅ | ✅ | ✅ | |
| `switch` 表达式 / 语句 | ✅ | ✅ | ✅ | ✅ | |
| 三目 `?:` / `??` / `?.` | ✅ | ✅ | ✅ | ✅ | |
| 字符串插值 `$"..."` | ✅ | ✅ | ✅ | ✅ | |
| 数组 `T[]` | ✅ | ✅ | ✅ | ✅ | |
| `List<T>` | ✅ | ✅ | ✅ | ✅ | pseudo-class |
| `Dictionary<K,V>` | ✅ | ✅ | ✅ | ✅ | pseudo-class，key→string |
| 可空类型 `T?`（隐式包装） | ✅ | ✅ | ✅ | ✅ | |
| 枚举 `enum` | ✅ | ✅ | ✅ | ✅ | 成员值映射为 i64 |
| 类（字段、构造器、方法） | ✅ | ✅ | ✅ | ✅ | |
| 异常 `try`/`catch`/`throw` | ✅ | ✅ | ✅ | ✅ | |
| 默认参数值 | ✅ | ✅ | ✅ | ✅ | call site 展开 |
| C# 数值类型别名 | ✅ | ✅ | ✅ | ✅ | |
| Math / Assert / Console | — | ✅ | ✅ | ✅ | pseudo-class |
| `extern` + `[Native]` InternalCall | ✅ | ✅ | ✅ | ✅ | stdlib interop |
| stdlib linking (StdlibCallIndex) | — | — | ✅ | ✅ | user code → CallInstr → stdlib stub → builtin |
| 表达式体方法 `=> expr;` | ✅ | ✅ | ✅ | ✅ | TopLevelParser |
| `struct` / `record` | ✅ | ✅ | ✅ | ✅ | struct 复用 class 路径；record 自动合成ctor |
| 接口 `interface` | ✅ | ✅ | ✅ | ✅ | 通过 VCallInstr 实现运行时分发 |
| 继承 | ✅ | ✅ | ✅ | ✅ | base(...) 构造器链支持 |

---

## L2 — Foundation（基础设施）

**目标**：在 L1 pipeline 基础上，补全编译器覆盖、稳定工程体系、建立测试基线、提升 VM 质量，落地基础标准库。

### 编译器完善
- TypeChecker 完整覆盖 L1 所有特性（struct、record、interface、inheritance）
- IR Codegen 完整覆盖 L1 所有特性
- 错误体系完善：统一错误码（`E####`）、友好错误消息、`explain <CODE>` 命令
- `.z42bc` 二进制格式稳定（magic、版本号、section layout 固定）
- `disasm` 反汇编输出可读性

### 工程支持
- `z42.toml` 项目清单：多 binary target、lib target、依赖声明
- `build`/`check`/`run`/`clean` 子命令完整
- 包格式 `.zpkg` 稳定（indexed/packed 模式、版本信息）

### 测试体系
- Golden test 覆盖所有 L1 特性（每特性至少：正常用例、边界用例、错误用例）
- VM interp + JIT 双模式运行同一测试集，结果一致
- CI 脚本稳定：`dotnet test` + `./scripts/test-vm.sh` 全绿为唯一合并门禁
- 自动化测试，减少大模型AI去检索日志，降低token使用

### VM 质量
- 类型元数据：type info、字段布局、方法表（为 L3 泛型/接口分发做准备）
- 调试符号：行号映射、局部变量名（支持基础调试体验）
- Interpreter 基础优化：指令 dispatch 效率、对象分配路径
- JIT 基础优化：热点函数识别、简单内联、常量折叠

### 标准库（基础）
- `z42.core`：基础类型协议（ToString、Equals、GetHashCode）
- `z42.io`：文件读写、标准输入输出
- `z42.collections`：`List<T>`、`Dictionary<K,V>` 原生实现（替代 pseudo-class 策略）
- `z42.string`：字符串操作完整实现

---

## L3 — Advanced（高级特性）

**目标**：引入 L1 推迟的高级语法，以及 z42 特有的类型系统扩展。L2 全完成后启动。

### 高级语法（从 L1 推迟）

| 特性 | 说明 |
|------|------|
| 泛型 `<T>` + `where` 约束 | 类型参数、约束推断、单态化编译 |
| Lambda + 闭包 | 捕获变量、`Func<>`/`Action<>` 委托 |
| 接口完整实现 | 多接口、虚方法表、接口继承 |
| 类继承完整实现 | 多态、`override`/`virtual`/`abstract` |
| `async`/`await` | `Task`/`ValueTask`、结构化并发 |
| LINQ 风格 | `Where`/`Select`/`OrderBy`/`ToList` 等 |
| 命名参数 | call site 指定参数名（`Greet(name: "z42")`） |
| 模式匹配扩展 | 属性模式、位置模式、`is` 类型测试 |

### z42 特有扩展

| 特性 | 说明 |
|------|------|
| `Result<T, E>` + `?` 运算符 | 函数式错误处理，`try`/`catch` 的高效替代 |
| `Option<T>` | 替代 `T?`，编译期穷尽检查，消除 null |
| Trait | 接口静态分发（零开销抽象），替代虚方法表 |
| ADT（代数数据类型） | 原生 sum type，替代 `abstract record` 模拟 |
| `match` 穷尽检查 | 强制覆盖所有分支，替代 `switch` |
| 默认不可变变量 | `let` 不可变，`var`/`mut` 显式可变 |
| 单文件脚本模式 | 无需 `z42.toml`，直接执行 `.z42` 文件 |
| 内联 eval | `z42vm -c "..."` 字符串直接执行；嵌入 API（host 传入 source/bytecode） |
| REPL | 交互式求值环境 |

---

## 实现里程碑（pipeline 维度）

| 里程碑 | 内容 | 所属阶段 | 状态 |
|--------|------|:-------:|:----:|
| M1 | Lexer + Parser | L1 | ✅ |
| M2 | TypeChecker（L1 特性全覆盖） | L1 → L2 | ✅ |
| M3 | IR Codegen → `.z42bc`（L1 特性全覆盖） | L1 → L2 | ✅ |
| M4 | VM Interpreter（L1 特性全覆盖） | L1 | ✅ |
| M5 | VM JIT（Cranelift，L1 特性） | L1 → L2 | ✅ |
| M6 | 工程支持 + 测试体系 + `.z42bc` 格式稳定 | L2 | 📋 |
| M7 | VM 元数据 + 标准库基础（core/io/collections） | L2 | 🚧 |
| M8 | TypeChecker + Codegen 扩展（L3 特性） | L3 | 📋 |
| M9 | VM AOT（LLVM/inkwell） | L3 | 📋 |
| M10 | 自举（Self-hosting） | L3+ | 📋 |

**当前焦点：M6（工程支持 + 测试体系 + 错误码体系）→ M7（VM 元数据 + 标准库）**
