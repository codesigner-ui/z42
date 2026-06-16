# Design: Type.IsClass / Type.IsInterface

## Architecture

```
编译期                                         wire (.zbc 1.19)          运行期
────────────────────────────────────────     ───────────────────       ────────────────────────
cu.Classes    → EmitClassDesc (flags bit4=0)
cu.Interfaces → EmitInterfaceDesc            TYPE section 每条目          read_type → TypeDesc.class_flags
  IrClassDesc{ IsInterface=true,             class_flags: u8             │
    IsAbstract=true, base=null,                bit4 = interface          ▼
    Fields=[], TypeParams }        ────────►  （bit0-3 不变）   ────────► IsClass     = handle && !struct && !iface
                                                                          IsInterface = handle && iface
```

接口此前完全不产 TYPE 条目（`IrGen.Generate.cs` 只 `cu.Classes.Select(EmitClassDesc)`）。本变更
让接口产**最小 ClassDesc**——复用既有 TYPE section 格式（无新字段，仅 flags 字节扩 bit4）。

## Decisions

### Decision 1: 接口产最小 ClassDesc（不含成员表）

**问题**：接口 TYPE 条目放多少信息？

**决定**：最小——`Name` + `TypeParams` + flags（interface + abstract）+ 空 fields/static/interfaces。
**不含**方法表（`typeof(IFoo).GetMethods()` 暂返空）。理由：本变更目标是类别谓词 + 让接口可解析到
handle（`Name`/`IsAbstract`/`IsGenericType` 顺带可用）；接口成员枚举是更大的独立工作（接口方法签名
持久化），延后。最小条目复用现有格式、零新字段，仅 flags 扩位。

### Decision 2: flags bit4 = interface（复用 class_flags 字节）

**问题**：interface 类别位放哪？

**决定**：`class_flags` bit4（`1 << 4 = 16`）。bit0-3 已用（abstract/sealed/struct/record），bit4-7 空闲。
复用同一字节 = 无新 wire 字段，仅该字节语义扩展（仍需 minor bump：reader 须知道 bit4 含义 + 接口
现在产条目改变了 TYPE section 内容）。enum 类别位预留 bit5（IsEnum 落地时用）。

### Decision 3: IsClass / IsInterface 语义（对齐 C#）

| API | 定义 | C# 对齐 |
|-----|------|---------|
| `IsInterface` | `has handle && (class_flags & 16)` | 接口 → true |
| `IsClass` | `has handle && !(class_flags & 4 struct) && !(class_flags & 16 iface)` | class / record → true；struct / interface / enum / 基元 / 数组 → false |

- **记录是 class**：`record`（bit3）不带 struct 位 → IsClass true（C# record class）。
- **接口隐式 abstract**：`EmitInterfaceDesc` 置 `IsAbstract=true` → `typeof(IFoo).IsAbstract == true`（C# 对齐）。
- **handle-less 一律 false**：基元 / 数组 / enum（本变更不产条目）无 handle → IsClass 与 IsInterface 都 false。
  数组 C# 是 `IsClass==true`，z42 数组 name-only synthetic → false，记 Deferred。

## Implementation Notes

- **EmitInterfaceDesc**（IrGen.Classes.cs，镜像 EmitClassDesc 简化版）：
  ```
  new IrClassDesc(QualifyName(iface.Name), BaseClass: null,
      Fields: [], TypeParams: iface.TypeParams,
      TypeParamConstraints: BuildConstraintList(iface.Name, iface.TypeParams, ...where),
      Attributes: null,
      IsAbstract: true, IsSealed: false, IsStruct: false, IsRecord: false,
      IsInterface: true,
      StaticFields: [], Interfaces: null)
  ```
- **Generate**：`var classes = cu.Classes.Select(EmitClassDesc).Concat(cu.Interfaces.Select(EmitInterfaceDesc)).ToList();`
- **ZbcWriter** flags：`| (cls.IsInterface ? 16 : 0)`。**ZbcReader**（C# round-trip）：`IsInterface: (flags & 16) != 0`。
- **runtime**：`CLASS_FLAG_INTERFACE = 1 << 4`；`builtin_type_is_interface`（class_flag_set INTERFACE）；
  `builtin_type_is_class`（`type_handle && flags & STRUCT == 0 && flags & INTERFACE == 0`）。flags 字节读取
  路径不变（zbc_reader 已读该字节），仅版本常量 bump。
- **风险核查**：接口产 type_registry 条目 →（a）`x is IFoo` / `as IFoo` 走对象的接口实现检查（不查 IFoo
  自身条目），不受影响；（b）vtable/字段空，read_type 须容空 base（结构体已验证此路径）；（c）接口名与
  类名 FQ 冲突——z42 不允许同名，非问题。golden + 全量 GoldenTests 验证无回归。

## Testing Strategy

- **golden（interp+jit）**：`interface_class_predicates.z42` —— `typeof(IFoo).IsInterface==true`/`IsClass==false`；
  `typeof(C).IsClass==true`/`IsInterface==false`；`record R` IsClass true；`struct S` IsClass false（IsValueType true）；
  基元 `int` 两者 false；`typeof(IFoo).IsAbstract==true`、`.Name=="IFoo"`（接口现有 handle）。
- **回归**：全量 dotnet GoldenTests（接口产条目不破坏 `is`/`as`/继承）+ ZbcReader round-trip。
- **格式 fixture**：zbc/zpkg regen。
- **GREEN**：dotnet + cargo + xtask vm/cross-zpkg/stdlib。`xtask test compiler-z42` 暂红（z42c writer 延后）。
