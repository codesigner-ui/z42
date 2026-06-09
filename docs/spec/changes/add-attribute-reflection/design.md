# Design: 用户自定义 Attribute + 反射

## Architecture

```
parse     [Route("/u", method: "POST")]  on class/method
            │  TopLevelParser 收集 → AttributeApp(Name="Route", Args=[..])
            ▼  挂到 ClassDecl.Attributes / FunctionDecl.Attributes
bind      AttributeBinder（TypeCheck 后期）
            │  ① Name → 解析 attribute 类（须派生 Std.Attribute，否则 E09xx）
            │  ② Args → 解析 ctor（复用 named-arg + 默认值绑定），逐参校验编译期常量
            ▼  绑定结果：(AttrClass, 解析后的 ctor 调用)
synth     AttributeFactorySynthesizer（codegen 前）
            │  每个应用 → 合成 FunctionDecl:
            │    Std.Attribute __attr_factory_<owner>_<i>() { return new Route("/u", "POST"); }
            │  加入 cu.Functions（BenchmarkDesugar 模式）→ 正常 IR + func_index
            ▼  记录：owner 声明 → [(TypeName="Route", FactoryFunc="__attr_factory_..")]
emit      IrGen.Generate → ExportedClassDef/MethodDef.Attributes = [ExportedAttributeRef..]
            ▼  ZbcWriter/ZpkgWriter 序列化（VersionMinor bump）
load      runtime zbc_reader → TypeDesc.cold.custom_attributes: Vec<AttributeRef{type_name, factory_func}>
reflect   Type.GetCustomAttributes()  → builtin_type_custom_attributes
            │  首次：对每个 AttributeRef，按 factory_func 名查 func_index → 调用（无参）→ Attribute 实例
            │  缓存到 TypeDesc 侧（或 type 对象槽）
            ▼  返回 Attribute[]（后续调用返回缓存的同一批实例）
```

## Decisions

### Decision 1: attribute = 派生 `Std.Attribute` 的普通类（无 `attribute` 关键字、无后缀）

**问题：** 如何声明 attribute？
**选项：** A — 专用 `attribute Foo { }` 关键字（意图清晰，可挂 target 子句）；B — 普通 `class Foo : Attribute`（复用类机制，零新语法）。
**决定：** 选 B。z42 偏好"简单清晰、少修饰符/关键字"（feedback_pragmatic_feature_adoption）。attribute 就是个类，`Std.Attribute` 标记它可作 attribute。**改进 #1**：应用按真实类名 `[Foo]`，**无 `Attribute` 后缀魔法**——不接受 `[FooAttribute]`，单一拼法、零隐式改名。专用关键字 + target 子句留作将来（AttributeUsage 一等化）。

### Decision 2: 单一构造器初始化路径（改进 #2）

**问题：** attribute 状态如何赋值？C# 用 positional→ctor + named→public 字段两条路径。
**决定：** **全部走构造器**。z42 已有 named-arg（`[add-named-arguments]`）+ 默认参数值，`[Route("/u", method: "POST")]` 直接映射 `new Route("/u", method: "POST")`。无"旁路写 public 字段"。构造器是唯一入口 → 唯一 invariant 守卫点。复用既有 ctor 重载解析 + Z0501 named-arg 诊断。

### Decision 3: factory-thunk 产活实例，待在 0.3.x 边界内（核心机制）

**问题：** `GetCustomAttributes()` 要返回**活实例**，但 0.3.x 明确推后 `Activator.CreateInstance`/`Method.Invoke`（依赖泛型实例化）。
**洞察：** attribute 构造是**全编译期已知**的——已知类、已知 ctor、常量参数。无需运行时泛型实例化。
**决定：** 编译期为每个应用合成**无参工厂函数** `Std.Attribute __attr_factory_N() { return new Foo(args); }`（args 是编译期常量，烘进函数体）。元数据存 `(TypeName, FactoryFuncName)`。反射调工厂。
- 复用既有合成模式（BenchmarkDesugar.SynthesizeWrapper；property get_/set_）——合成 FunctionDecl 加 cu.Functions，IrGen 正常分配 func_index。
- **零新运行时实例化机制**，与 0.5.x 泛型/Invoke 工作不冲突。
- **副带改进**：args 烘进工厂体 → z42 **不需要** C# 的元数据参数 blob 编解码。工厂函数即"序列化形式"，元数据只存两个字符串。

**选项对比：**
- A（采纳）factory-thunk：编译期烘常量 + 合成函数。复用现有 codegen，元数据极简。
- B 运行时受限 Activator：元数据存 ctor ref + 序列化常量参数，运行时实例化。需新运行时实例化路径 + 参数序列化格式，更接近被推后的 Activator。✗

### Decision 4: 缓存不可变单例（改进 #3 + #4）

**问题：** C# 每次 `GetCustomAttributes()` 重建实例（因实例可变，必须给每个 reader 独立副本）。
**决定：** **首次反射时调工厂实例化一次，缓存，后续返回同一实例**（改进 #3）。安全性来自**不可变约定**（attribute 字段 ctor 内一次写定，改进 #4）。MVP 不强制 init-only（等 init-only 支持），靠约定 + 缓存拿到性能 + 身份稳定。缓存位置：TypeDesc 侧 `OnceCell`/lazy 槽（Rust），key=owner type。

### Decision 5: target 校验推后（改进 #5）

MVP 不做 `AttributeUsage`/target 限制——attribute 可标 class/method 任意位，不校验"该 attribute 是否允许标在此"。将来加时做**声明上的一等子句**（非 C# 的元属性自循环），且默认更合理（AllowMultiple=true、无隐式继承）。记 attributes.md Deferred。

### Decision 6: 元数据格式 + 版本 bump

`ExportedClassDef` / `ExportedMethodDef` 加 `List<ExportedAttributeRef>? Attributes`，`ExportedAttributeRef = { string TypeName, string FactoryFunc }`。
- `ZbcWriter.VersionMinor` 9→10、`ZpkgWriter.VersionMinor` 11→12、runtime `ZBC_VERSION_MINOR` 同步。
- 按 [version-bumping.md](../../../../.claude/rules/version-bumping.md) checklist 改全部点 + `docs/design/runtime/zbc.md` changelog。
- pre-1.0 不留旧格式兼容（philosophy 不为旧版本兼容）——旧 .zbc 不可读，regen 即可。
- **跨 zpkg**：FactoryFunc 存**限定名**，跨模块 func 解析（同 C2 `resolve_func_sig` / make_type_from_name 路径），包 B 可调包 A 的工厂。

## Implementation Notes

- **AttributeApp AST**：`AttributeApp(string Name, List<Argument> Args, Span)`（`Argument` 含 Name?+Expr Value，复用 Ast.cs:405）。`ClassDecl`/`FunctionDecl` 加 `List<AttributeApp>? Attributes`。parser 当前在 class 前**静默丢弃** attribute（TopLevelParser.Types.cs:318–323）→ 改为收集。
- **AttributeBinder** 时机：在类型/符号齐备后（attribute 类可能后于使用点声明 → 需符号表就绪）。常量校验：Args 的 Expr 必须是字面量 / enum 成员 / `typeof`（复用常量折叠判定）。
- **合成命名**：`__attr_factory_<OwnerQualified>_<index>`，确保唯一 + 不撞用户名（`__` 前缀）。
- **缓存 Rust 侧**：`TypeDescCold` 加 `custom_attributes: Box<[AttributeRef]>` + 一个 `OnceCell<Vec<Value>>` 缓存实例（或在 Std.Type 对象上挂缓存槽——避免 TypeDesc 持 GC 引用的生命周期问题，倾向后者：缓存挂 type 对象）。
- **错误码**：E0920 非 attribute 类、E0921 无匹配 ctor、E0922 非常量参数（具体号 implementation 期定，避开已用 E0605/E0913）。

## Testing Strategy
- **C# 单元**：AttributeParseTests（解析 + AST 挂载 class/method）、AttributeBindTests（E0920/0921/0922 三类错误）。
- **golden**（权威）：`src/tests/attributes/basic.z42` —— 声明 Route + 应用 class/method + GetCustomAttributes 读字段 + 缓存同一实例 + GetAttribute(typeof)。
- **z42 [Test]**：`z42.core/tests/attributes.z42`。
- **cross-zpkg**：包 A 定义 attribute + 应用，包 B 反射读取（持久化验证）。
- **GREEN**：`xtask test`（vm/cross-zpkg/lib）；C# GoldenTests 权威。zbc 格式变 → regen 残留 golden .zbc。
