# Design: L3-G1 泛型基础

## Architecture

```
源码                        编译时                       运行时
Identity<T>(T x)    →   TypeChecker:               →  VM: 
var r = Identity<int>(42)   T=int 替换验证               call @Identity %0
                         IrGen: 一份共享代码              (Value 天然类型擦除)

Box<T> { T value; }  →   TypeChecker:               →  VM:
new Box<int>(42)         T=int, value:int 验证           ObjNew → TypeDesc{type_args=["int"]}
```

## Decisions

### Decision 1: `<T>` 解析歧义
**问题：** `foo<bar>(x)` 是泛型调用还是 `foo < bar > (x)` 两次比较？
**决定：** 在已知泛型声明的上下文中（函数名后、类名后、`new` 后）解析为泛型参数。表达式中的独立 `<` 仍为比较。Parser 通过声明上下文（不在表达式 Pratt loop 中）消歧义。

### Decision 2: 类型参数在 IR 中的表示
**问题：** 泛型参数 T 在 IR 类型系统中如何表示？
**决定：** T 在 IR 中映射为 `IrType.Ref`（通用引用类型）。字节码层面没有 T 概念——类型检查完全在编译时完成，运行时通过 TypeDesc.type_args 提供反射信息。

### Decision 3: 泛型类的字段类型
**问题：** `class Box<T> { T value; }` 的 value 字段在 ClassDesc 中是什么类型？
**决定：** 字段类型存为 "T"（字符串），TypeChecker 在实例化时替换验证。IR ClassDesc 中类型标记为 "object"（代码共享，运行时按 Value 处理）。

### Decision 4: 泛型实例化 TypeDesc 缓存
**问题：** `new Box<int>(42)` 和 `new Box<int>(99)` 共享同一个 TypeDesc 吗？
**决定：** 是。VM 维护 `(generic_name, type_args) → Arc<TypeDesc>` 缓存。相同类型参数的实例化共享 TypeDesc。

## Implementation Notes

### Parser 变更
- `ParseClassDecl`: `SkipGenericParams` → `ParseTypeParams` 返回 `List<string>`
- `ParseFunctionDecl`: 检测 `Ident<` 模式，解析类型参数
- `TypeParser`: 新增 `GenericType(Name, TypeArgs)` — 解析 `Box<int>`、`Dict<string, int>`

### TypeChecker 变更
- 新增 `_typeParamScope: Dictionary<string, Z42Type>` 管理泛型参数作用域
- 泛型函数调用时 push scope（T=int），检查完 pop
- 泛型类实例化时 push scope，验证字段和方法签名

### IrGen 变更
- IrFunction 新增 `TypeParams: List<string>?` 字段
- ClassDesc 保持不变（字段类型按 IR 类型写入，T → "object"）

### VM 变更
- TypeDesc 新增 `type_params: Vec<String>`, `type_args: Vec<String>`
- `ObjNew` 指令扩展：类名可含泛型参数 `Box<int>`
- VM 解析 `Box<int>` → 查找 `Box` TypeDesc → 实例化带 type_args=["int"]

## Testing Strategy
- Golden test: `68_generic_function` — 泛型函数定义和调用
- Golden test: `69_generic_class` — 泛型类定义、实例化、方法调用
- 单元测试: TypeChecker 泛型参数替换
- ZbcRoundTrip: type_params 字段序列化
