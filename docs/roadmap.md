# z42 Roadmap

## 固定决策

- **GC**：z42 始终带 GC，不引入所有权/借用（降低上手成本）
- **IR**：寄存器 SSA 形式
- **执行模式注解**：作用于命名空间级
- **`.zbc` magic**：`ZBC\0`

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
- `.zbc` 二进制格式稳定（magic、版本号、section layout 固定）
- `disasm` 反汇编输出可读性

### 工程支持
- `z42.toml` 项目清单：多 binary target、lib target、依赖声明
- `build`/`check`/`run`/`clean` 子命令完整 ✅
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

### 代码质量 Backlog（按触发条件执行）

> 来源：2026-04-14 代码审查。批次 1–4 已完成，以下为剩余低优先级项。

| 项目 | 触发条件 | 说明 |
|------|---------|------|
| A6: Value `Rc<RefCell>` → `Arc<Mutex>` 或对象池 | L3 async/线程模型设计时 | `Rc` 是 `!Send`，阻塞跨线程传值；需与并发模型一并设计 |
| A10: `PackageCompiler` → 可注入 `BuildPipeline` | 需要 mock 文件系统做编译器单元测试时 | 当前 static class 可用，低优先级 |
| `TypeEnv.BuiltinClasses` 动态注入 | L3 泛型设计启动时 | 当前硬编码集合；与泛型一并设计 |
| `IsReferenceType` 中 List/Dict 硬编码 | L3 泛型设计启动时 | List/Dict 应为 `Z42ClassType`，需泛型类型表示 |
| switch 穷举检查（exhaustiveness） | enum switch 场景增多时 | switch on enum 不检查是否覆盖所有成员 |
| 死代码警告 | IDE 集成或用户反馈时 | return 后语句静默丢弃，应发 warning |
| 隐式窄化转换拒绝 | 数值精度 bug 出现时 | `int x = someLong` 应报错要求显式 cast |
| `IrInstr` JsonDerivedType 自动注册 | 指令数超过 60 个时 | 当前 54 个注解，可考虑 Source Generator 方案 |
| `exec_instr.rs` 按类别拆分辅助函数 | 文件超过 450 行时 | 当前 362 行，保持单 match 结构但提取 arm 实现 |
| Golden Test 改用 `test.toml` 声明类别 | 测试目录结构变复杂时 | 当前路径约定 (`/errors/`, `/run/`) 工作正常 |

---

## L3 — Advanced（高级特性）

**目标**：引入 L1 推迟的高级语法，以及 z42 特有的类型系统扩展。L2 全完成后启动。

### L3-G 泛型实现进度

| 子阶段 | 内容 | Parser | TypeCheck | IrGen | VM | 状态 |
|--------|------|:------:|:---------:|:-----:|:--:|:----:|
| **L3-G1** | 泛型函数 + 泛型类（无约束） | ✅ | ✅ | ✅ | ✅ | ✅ |
| **L3-G2** | 接口约束（`where T: I + J`） | ✅ | ✅ | — | — | ✅ |
| **L3-G2.5** | 约束范式补充：基类 ✅ / 构造器 / class / struct / notnull 等 | 🟡 | 🟡 | — | — | 🟡 |
| **L3-G3a** | zbc 约束元数据 + VM loader + 加载时校验 | — | — | ✅ | ✅ | ✅ |
| **L3-G3c** | 关联类型（`type Output; Output=T`） | — | — | — | — | 📋 |
| **L3-G3d** | 跨 zpkg TypeChecker 消费约束（TSIG 扩展） | — | ✅ | ✅ | — | ✅ |
| **L3-G4a** | 泛型类实例化类型替换（call-site T → 具体类型） | — | ✅ | — | — | ✅ |
| **L3-G4b** | Primitive 类型实现 interface（`Max<int>` 可用） | — | ✅ | — | ✅ | ✅ |
| **L3-G4c** | User-level 泛型容器源码实现（MyList<T> 端到端 demo） | — | ✅ | — | ✅ | ✅ |
| **L3-G4d** | stdlib 导出泛型类（Std.Collections.Stack / Queue 启用 + 名称冲突裁决 + 懒加载 ctor） | — | ✅ | ✅ | ✅ | ✅ |
| **L3-G4e** | 索引器语法 `T this[int] { get; set; }` — desugar 到 get_Item/set_Item | ✅ | ✅ | — | — | ✅ |
| **L3-G4f** | 源码级 ArrayList<T> ✅；HashMap 放到 G4g | — | 🟡 | — | — | 🟡 |
| **L3-G4g** | 跨命名空间约束解析 ✅ + ArrayList.Contains/IndexOf ✅ + HashMap<K,V> ✅ + TSIG 不重导入 ✅ | — | ✅ | — | — | ✅ |
| **L3-G4h** | step1 `&&`/`||` 短路求值 ✅；step2 foreach 鸭子协议 ✅；step3 pseudo-class List/Dict → 源码 ✅ | — | ✅ | ✅ | ✅ | ✅ |
| **L3-G4** | 泛型标准库（已细拆为 G4a/G4b/G4c/G4d，保留总指标） | — | 🟡 | — | 🟡 | 🟡 |
| **L3-R** | 反射与运行时类型信息 — 见下独立小节（统一批次，延后） | — | — | — | — | 📋 |

> L3-G1 已实现：泛型函数/类定义、显式/推断类型参数、IR 代码共享、SIGS/TYPE section 携带 `type_params`。
> L3-G2 已实现：`where T: I + J` / `where K: I, V: J` 语法、约束方法查找、调用点校验、返回类型按推断替换；启用 `IComparable<T>` / `IEquatable<T>` stdlib 接口。
> L3-G3a 已实现：zbc 版本 0.4 → 0.5，SIGS/TYPE per-tp 约束元数据；Rust VM loader 读取到 `TypeDesc.type_param_constraints` / `Function.type_param_constraints`；加载时 `verify_constraints` pass 校验约束引用的 class/interface 存在（`Std.*` 前缀放行给 lazy loader）。**不**做运行时 Call/ObjNew 校验（留给 L3-G3b 配合反射）。

### L3-G2.5：约束范式扩展（计划）

L3-G2 仅实现 interface 约束。以下范式按优先级排期，每项独立规格：

| 约束 | 语法 | 语义 | 优先级 / 状态 |
|------|------|------|:------:|
| **基类约束** | `where T: BaseClass` | T 必须继承自指定类；可访问基类字段/方法 | ✅ 已完成（2026-04-22） |
| **构造器约束** | `where T: new()` | T 必须有无参构造器；支持 `new T()` 泛型体内实例化 | 高（依赖 L3-G3a） |
| **引用类型约束** | `where T: class` | T 为引用类型（排除 struct/primitive） | ✅ 已完成（2026-04-22） |
| **值类型约束** | `where T: struct` | T 为值类型 | ✅ 已完成（2026-04-22） |
| **非空约束** | `where T: notnull` | T 非空（排除 `T?`） | 中 |
| **接口继承约束** | `where T: I<U>, U: J` | 跨参数约束链（已部分支持，补齐校验） | 中 |
| **裸类型参数约束** | `where U: T` | U 必须是 T 的子类型（T 为同 decl 其他 type param） | ✅ 已完成（2026-04-22） |
| **委托/函数约束** | `where T: Func<...>` | 可调用约束（依赖 Lambda L3 其他子阶段） | 低 |
| **枚举约束** | `where T: enum` | T 为枚举类型 | 低 |
| **变型标注** | `interface IFoo<in T>` / `<out T>` | 协变/逆变 | 延后 L3 后期 |
| **默认类型参数** | `class Box<T = int>` | 省略时默认值 | 延后 L3 后期 |

**实现策略**：
- 基类 + 构造器约束复用现有 interface 约束框架（Z42GenericParamType 的 Constraints 扩展为 union: Interface / BaseClass / ConstructorReq / ValueKind）
- `class`/`struct`/`notnull` 作为 "flags" 附加在 GenericParam 上
- 每个范式独立 openspec change，共享 L3-G3a 的约束元数据 zbc 扩展

### L3-R：反射与运行时类型信息（统一批次，延后）

把原 L3-G3b（反射接口 + 运行时约束校验）与其他反射需求合并成独立轨道，一次性规划
VM 运行时类型系统。多项特性联动，单独做不如合并。

| 子项 | 内容 | 依赖 |
|------|------|------|
| **R-1 核心 Type API** | `typeof(T)` / `t.GetType()` / `Type.Name` / `Type.TypeParams` / `Type.TypeArgs` | L3-G3a（元数据已就绪） |
| **R-2 约束反射** | `Type.Constraints` / `Type.BaseClass` / `Type.Interfaces` | R-1 |
| **R-3 is/as 运行时判断** | `t is IComparable<T>` / `t as SomeBase` 基于 TypeDesc.vtable + constraints | R-1 + R-2 |
| **R-4 运行时 Call/ObjNew 约束校验** | 泛型函数 Call / `new T(...)` 时校验 type_args 满足约束（untrusted zbc 兜底） | R-1 + type_args 传递机制 |
| **R-5 运行时 type_args 传递** | 泛型实例化信息通过隐式参数 / thread-local / TypeDesc 引用传到 callee | 需 VM 架构决策 |
| **R-6 `new T()` 支持** | 依赖 R-5 拿到 T 的 TypeDesc，ObjNew 时用实际类名 | R-5 |
| **R-7 `Activator.CreateInstance<T>(args)`** | 反射式泛型实例化 | R-5 + R-6 |
| **R-8 Module / Assembly 反射** | `Module.GetTypes()` / `Type.GetMethods()` | R-1 |
| **R-9 关联类型反射** | `Type.AssocTypes["Output"]` | L3-G3c |
| **R-10 IDE / 工具 元数据** | 供外部工具（LSP / REPL）读取 TypeDesc 完整结构 | R-1 |

**设计挑战（为什么合并）**：
- R-5 运行时 type_args 传递是最核心的架构决策 — 决定 R-4/R-6/R-7 能否实现
- 单独做 R-1/R-2 意义有限（应用场景少，ROI 低）；和 R-4/R-5 一起才产生价值
- zbc 格式扩展若需要为 R-2/R-9 补字段，与 L3-G3a 的约束字段可一次设计完

**先决条件**：
- L3-G3a 已完成（元数据管道打通）✅
- L3-G3c 关联类型（R-9 依赖）
- VM 架构决策：type_args 如何在代码共享前提下运行时可得

### 高级语法（从 L1 推迟）

| 特性 | 说明 |
|------|------|
| 泛型 `<T>` + `where` 约束 | 类型参数、约束推断、代码共享 + 具化（L3-G1 ✅ 基础完成） |
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
| M3 | IR Codegen → `.zbc`（L1 特性全覆盖） | L1 → L2 | ✅ |
| M4 | VM Interpreter（L1 特性全覆盖） | L1 | ✅ |
| M5 | VM JIT（Cranelift，L1 特性） | L1 → L2 | ✅ |
| M6 | 工程支持 + 测试体系 + `.zbc` 格式稳定 | L2 | 📋 |
| M7 | VM 元数据 + 标准库基础（core/io/collections） | L2 | 🚧 |
| M8 | TypeChecker + Codegen 扩展（L3 特性） | L3 | 📋 |
| M9 | VM AOT（LLVM/inkwell） | L3 | 📋 |
| M10 | 自举（Self-hosting） | L3+ | 📋 |

**当前焦点：M6（工程支持 + 测试体系 + 错误码体系）→ M7（VM 元数据 + 标准库）**
