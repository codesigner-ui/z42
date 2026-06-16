# Design: typeof 携带泛型实例化 args

## Architecture

```
编译期                                wire (.zbc 1.18)           运行期
─────────────────────────────────    ──────────────────────    ──────────────────────────
typeof(Box<int>)
  BoundTypeof{ Z42InstantiatedType
    Definition=Box, TypeArgs=[int] }
        │ VisitTypeof
        ▼
  TypeofInstr{                        Opcode Typeof             Instruction::Typeof{
    Dst,                               dst reg                    dst, type_name,
    TypeName="…Box",          ──────►  TypeName STRS idx  ──────► type_args: Box<[String]> }
    TypeArgs=["int"] }                 count=1 + ["int"] idx[]         │ interp/jit
                                                                       ▼
                                                                 make_type_from_name("…Box")
                                                                   → 定义 TypeDesc handle
                                                                 + 挂 type-args 槽 [typeof(int)]
                                                                       │
                                                                       ▼
                                                            构造型 Std.Type:
                                                              IsGenericType=true
                                                              IsGenericTypeDefinition=false
                                                              GetGenericArguments()=[int]
                                                              GetGenericTypeDefinition()=Box<>
```

镜像两条已验证先例：
- **结构化 type-args 编码** = `ObjNewInstr.TypeArgs`（count + STRS 索引；reader 对称读）。
- **类型对象挂运行期类型槽** = 数组 `__elementName`（`build_type_ex` 写槽，反射读槽）。

## Decisions

### Decision 1: 统一所有 typeof 走新 opcode（移除 __typeof builtin）

**问题**：新 `Typeof` opcode 只接管泛型实例化的 typeof，还是接管全部？

**选项**：
- A — **全部 typeof 走 Typeof opcode**，移除 `__typeof` builtin。单一路径；type args 永远是
  指令元数据（非泛型时 count=0）。代价：每个含 typeof 的 golden .zbc 字节变化（regen 吸收）；
  JIT 要实现新 opcode。
- B — 仅泛型实例化 typeof 走新 opcode，其余仍 `__typeof` builtin。字节 churn 小，但两条路径、
  概念分裂。

**决定**：选 **A**。pre-1.0 取最干净单一路径（philosophy「不为破坏面退而求其次」）。type args 是
编译期类型元数据，统一编码为指令字段比"非泛型 ConstStr 值 + 泛型走别路"更一致。字节 churn 是
机械 regen。

### Decision 2: type-args 在 wire 上是 FQ 名列表（非递归结构）

**问题**：嵌套泛型 `Box<Map<K,V>>` 如何编码？

**决定**：MVP 每个 type-arg 是一个 FQ **名字符串**（STRS 索引），与 ObjNew 完全一致。
非嵌套 arg（`int` / `Std.String` / 用户裸类）由 `make_type_from_name` 直接解析。嵌套 arg
（其名仍含 `<>`）**延后**：runtime 暂不递归解析 arg 内的 `<>`，`GetGenericArguments()` 对嵌套
arg 返回按名解析的定义型（退化但不崩）。完整递归 = generic-type-definition Deferred 续作。

### Decision 3: 构造型 vs 定义型用 type-args 槽区分

**问题**：运行期如何区分 `Box<int>`（构造型）与 `Box<>`（定义型）？

**决定**：`Std.Type` 加一个**运行期 type-args 槽**（`__typeArgs`，存 `Std.Type[]`；空 = 无）。
- 构造型（typeof 带非空 TypeArgs）→ 槽非空。
- 定义型（`GetGenericTypeDefinition()` 产出，或非泛型）→ 槽空。

谓词语义（对齐 C#）：
| API | 定义 |
|-----|------|
| `IsGenericType` | `type_params` 非空（不变，已落地）|
| `IsGenericTypeDefinition` | `IsGenericType && __typeArgs 空`（泛型且未构造）|
| `GetGenericArguments()` | 返回 `__typeArgs` 槽（构造型 = 实例 args；定义型 = 空）|
| `GetGenericTypeDefinition()` | 剥掉 `__typeArgs`，返回 `make_type_from_name(base)` 定义型；非泛型抛 |

> 注：`GetGenericArguments()` 此前读 `TypeDesc.type_args()`（定义 TypeDesc 永远空）——本变更改读
> Type 对象的 `__typeArgs` 槽，修复 typeof 构造型返回空数组的 bug。`new Box<int>()` 实例的
> `obj.GetType()` 走另一路径（ScriptObject.type_args），不在本 MVP 改动（见 Deferred）。

## Implementation Notes

- **Opcode 选址**：取一个空闲 byte（Opcodes.cs 现有连续段尾后追加，避开 0x00–0x0F 常量 type-tag 段）。
- **codegen**：`VisitTypeof` 不再 `ConstStr + Builtin(__typeof)`，改 `Emit(new TypeofInstr(dst,
  Z42TypeName(baseDef), typeArgNames))`。`Z42TypeName(Z42InstantiatedType)` **保持只产定义名**
  （args 经 instr 字段携带，不混入名字串——避免 `"Box<int>"` 拼接歧义）。TypeArgs 各元素经
  `Z42TypeName(argType)` 取 FQ 名。
- **wire 编码**（ZbcWriter）：`Typeof` + dst type-tag + WriteReg(dst) + `pool.Idx(TypeName)` +
  `count u?` + `pool.Idx(arg)` ×count。intern pass 同步 intern TypeName + 各 arg。**字节序/计数宽度
  与 ObjNew type_args 保持一致**（对齐 reader）。
- **reader**（zbc_reader.rs）：镜像 ObjNew 读法（read TypeName idx → count → args）。
- **interp**：`Instruction::Typeof` → `make_type_from_name(type_name)` 得定义型 Type；若 type_args
  非空 → 逐个 `make_type_from_name(arg)` → 写入构造型的 `__typeArgs` 槽。空 → 直接返回定义型。
- **jit**：translate.rs 为 Typeof 生成调用 runtime helper（与 interp 同语义；不在 JIT 内联反射逻辑）。
- **`__typeArgs` 槽**：在 `Std.Type` 加字段（仿 `__elementName`），`build_type` 默认空数组；
  新增 `build_type_constructed(base_type, args)` 路径填槽。

## Testing Strategy

- **单元（C#）**：`TypeofInstr` codegen → 正确 opcode + TypeName + TypeArgs 编码；ZbcWriter
  round-trip（ReadWriteRoundTrip）。
- **golden（interp+jit）**：`src/tests/types/generic_type_definition.z42` —— `typeof(Box<int>)`
  的 `IsGenericType`/`IsGenericTypeDefinition`/`GetGenericArguments()`/`GetGenericTypeDefinition()`；
  定义型经 GetGenericTypeDefinition 取回后 `IsGenericTypeDefinition==true`；多参 `Pair<int,string>`。
- **格式 fixture**：`zbc-format` / `zpkg-format` regen，git diff 显示格式 delta。
- **GREEN**：dotnet test（含 Zbc/Zpkg invariant）+ cargo lib + xtask vm/cross-zpkg/stdlib。
- **已知红**：`xtask test compiler-z42` byte-identical（z42c writer 未同步，Deferred）。
