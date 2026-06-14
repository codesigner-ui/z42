# Proposal: Type.GetInterfaces() — 类实现的接口反射

## Why

反射已覆盖字段/方法/属性/泛型参数/特性/类修饰符，但 **`Type.GetInterfaces()` 缺失**——
无法反射出一个类实现了哪些接口。`IrClassDesc.Interfaces`（绑定期接口名列表）
在 C# IR 模型里已由 codegen（`IrGen.Classes.cs`）填充，但 **从未写入 zbc TYPE
section**，故运行期 `TypeDesc` 拿不到接口名。这是 C# `Type.GetInterfaces()` 的
直接对应物，也是 0.3.x 相位内（不依赖泛型实例化）能干净落地的最常用缺失 API。

## What Changes

- zbc TYPE section 每个类记录在静态字段块之后追加 **接口块**：`interface_count: u16`
  + `interface_name_idx[]: u32`（接口 FQ 名，string-pool idx）。格式 bump zbc 1.16→1.17 / zpkg 0.18→0.19。
- C# `ZbcWriter.BuildTypeSection` 写接口块 + intern 预扫；`ZbcReader.ReadTypeSection`
  读回 → `IrClassDesc.Interfaces`（round-trip parity）。
- Rust `zbc_reader.rs` 读接口块 → `ClassDesc.interfaces`；`TypeDescCold` 新增
  `interfaces` 字段（cold，反射专用）。
- Rust `reflection.rs` 新增 `builtin_type_interfaces`（`__type_interfaces`）：
  沿 base 链聚合本类 + 继承接口（base-walk，按名 dedup，镜像 inherited-static-fields），
  每个接口名经 `make_type_from_name` 还原为 `Std.Type`。
- `Type.z42` 新增 `public extern Type[] GetInterfaces();`。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `IrClassDesc` record 加 `Interfaces` 字段（实施期识别：接口名的 IR 载体）|
| `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` | MODIFY | `EmitClassDesc` 从 `ClassDecl.Interfaces` 填 `IrClassDesc.Interfaces` + `InterfaceTypeName` 助手（实施期识别：codegen 填充端）|
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `VersionMinor`=17；BuildTypeSection 写接口块；intern 预扫 interface 名 |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | ReadTypeSection 读接口块 → IrClassDesc.Interfaces |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor`=19（zbc 1.17 联动） |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `ZBC_VERSION_MINOR`=17 / `ZPKG_VERSION_MINOR`=19；read_type 读接口块 → ClassDesc.interfaces |
| `src/runtime/src/metadata/zbc_reader_tests.rs` | MODIFY | version-pin 断言 17/19 |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `ClassDesc.interfaces` 字段（实施期识别：ClassDesc 在 bytecode.rs 非 types.rs）+ register_lazy_type clone |
| `src/runtime/src/metadata/types.rs` | MODIFY | `TypeDescCold.interfaces` + `interfaces()` accessor |
| `src/runtime/src/metadata/loader.rs` | MODIFY | ClassDesc→TypeDescCold load 透传 interfaces（实施期识别：cold 构造点）|
| `src/runtime/src/metadata/{loader,merge,constraint}_tests.rs` | MODIFY | ClassDesc 字面量补 interfaces 字段（实施期识别：测试构造点）|
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_type_interfaces`（base-walk + dedup + make_type_from_name） |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 `__type_interfaces` |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `public extern Type[] GetInterfaces();` |
| `docs/design/runtime/zbc.md` | MODIFY | Minor changelog 加 1.17 行 + TYPE section 接口块布局 |
| `docs/design/runtime/zpkg.md` | MODIFY | Minor changelog 加 0.19 行 |
| `docs/design/language/reflection.md` | MODIFY | GetInterfaces 用法 + 实现原理；Deferred 加 `reflection-future-get-interfaces` 落地标记 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 占用 compiler/runtime/stdlib；归档时释放 |
| `src/tests/types/get_interfaces.z42` | NEW | golden e2e（单接口/多接口/继承接口/无接口/接口对象 GetType） |

**只读引用**（理解上下文，不改）：

- `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` — 确认 IrClassDesc.Interfaces 填充来源
- `src/runtime/src/corelib/reflection.rs`（`builtin_type_fields` base-walk）— inherited-static-fields 同款聚合模式参考
- `.claude/rules/version-bumping.md` — 格式 bump checklist

## Out of Scope

- **接口的传递实现**（interface-extends-interface，如 `IList : ICollection` → GetInterfaces 含 ICollection）：需接口类型元数据 + 接口继承图，本变更只做"类直接声明 + 类继承链聚合"。传递闭包入 Deferred。
- **`Type.GetInterface(string)`**（按名查单个）/ `IsAssignableFrom`：后续小增量，不在本变更。
- **z42c 自举 writer 同步**：z42c 子系统当前工作树有未提交 WIP；本变更走 dotnet 权威门 + cargo + xtask，z42c 接口块镜像作为 follow-up 跟踪（沿用 add-reflection-array-element-type 的延后处理）。
- **接口本身的 `Type` 句柄成员枚举**（接口不产 TYPE 条目）：`GetInterfaces()` 返回的接口 Type 是 name-only（与 typeof(IFoo) 一致），其成员枚举不在本变更。

## Open Questions

- [ ] GetInterfaces() 是否含继承接口（base 链）？→ design.md Decision 2（倾向：含，镜像 C# + inherited-static-fields 模式）
