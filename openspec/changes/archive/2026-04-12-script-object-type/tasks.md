# Tasks: Script Object Type and String Members

> 状态：🟢 已完成 | 创建：2026-04-12 | 完成：2026-04-12

## 进度概览
- [x] 阶段 1: Parser 属性语法
- [x] 阶段 2: VM 类型层
- [x] 阶段 3: VM Loader
- [x] 阶段 4: VM Interp
- [x] 阶段 5: VM Corelib
- [x] 阶段 6: Stdlib + IrGen
- [x] 阶段 7: 测试与验证

## 阶段 1: Parser 属性语法

- [x] 1.1 `TopLevelParser.ParseFunctionDecl`：`isExtern && LBrace` → `SkipAutoPropBody`

## 阶段 2: VM 类型层（types.rs）

- [x] 2.1 新增 `FieldSlot { name: String }`
- [x] 2.2 新增 `TypeDesc { name, base_name, fields, field_index, vtable, vtable_index }`
- [x] 2.3 新增 `NativeData { None, StringBuilder(String) }`
- [x] 2.4 新增 `ScriptObject { type_desc: Arc<TypeDesc>, slots: Vec<Value>, native: NativeData }`
- [x] 2.5 将 `Value::Object` 内部类型从 `Rc<RefCell<ObjectData>>` 改为 `Rc<RefCell<ScriptObject>>`
- [x] 2.6 更新 `Value::PartialEq` 中的 Object 分支
- [x] 2.7 在 `Module`（bytecode.rs）中新增 `type_registry: HashMap<String, Arc<TypeDesc>>`（默认空）

## 阶段 3: VM Loader（loader.rs）

- [x] 3.1 实现 `build_type_registry(module)` — 拓扑排序 ClassDesc，构建 TypeDesc
- [x] 3.2 继承链展平：base fields → derived fields（去重，derived 覆盖）
- [x] 3.3 vtable 展平：扫描 `module.functions`，找出属于该类的方法，构建 vtable + vtable_index
- [x] 3.4 在 `load_artifact` 完成后调用 `build_type_registry`；merge 后也调用
- [x] 3.5 单元测试（`loader_tests.rs`）：TypeDesc 字段 slot 正确；vtable 覆盖正确

## 阶段 4: VM Interp（interp/mod.rs）

- [x] 4.1 `ObjNew`：查 `module.type_registry` 得 `Arc<TypeDesc>`，分配 `slots: vec![Null; fields.len()]`，调用构造函数
- [x] 4.2 `FieldGet`：ScriptObject → slot 路径；`Value::Str` → 虚拟字段 `"Length"` (I64)；`Value::Array/Map` → `"Length"/"Count"` (I64)
- [x] 4.3 `FieldSet`：ScriptObject → slot 路径
- [x] 4.4 `VCall`：`type_desc.vtable_index[method]` → `vtable[slot].1`（qualified func）→ `exec_function`
- [x] 4.5 `IsInstance`：用 `type_desc.base_name` 链代替 `module.classes` 扫描
- [x] 4.6 `AsCast`：同上

## 阶段 5: VM Corelib（corelib/collections.rs + object.rs）

- [x] 5.1 `__sb_new`：分配 `ScriptObject { native: NativeData::StringBuilder(""), slots: [] }`
- [x] 5.2 `__sb_append` / `__sb_append_line` / `__sb_append_newline`：读写 `native.StringBuilder`
- [x] 5.3 `__sb_to_string` / `__sb_length`：从 `native.StringBuilder` 读取
- [x] 5.4 `obj_get_type` / `obj_ref_eq` / `obj_hash_code`（object.rs）：适配 `ScriptObject` API

## 阶段 6: Stdlib + IrGen

- [x] 6.1 `String.z42`：新增 `[Native("__str_length")] public extern int Length { get; }`
- [x] 6.2 `String.z42`：新增 `public bool IsEmpty { get { return Length == 0; } }`
- [x] 6.3 `StringBuilder.z42`：移除 `private string __data;` 字段
- [x] 6.4 `IrGenExprs.cs`：移除 `case MemberExpr m when m.Member is "Length" or "Count"` 特判
- [x] 6.5 `IrGenExprs.cs`：移除 `EmitNullConditional` 中的 `if (nc.Member is "Length" or "Count")` 特判

## 阶段 7: 测试与验证

- [x] 7.1 新增 `examples/string_members.z42`（Length、IsEmpty 示例）
- [x] 7.2 `IrGenTests.cs`：`arr.Length` 生成 `field_get "Length"`，不生成 `builtin "__len"`
- [x] 7.3 `dotnet build src/compiler/z42.slnx` — 无编译错误
- [x] 7.4 `cargo build --manifest-path src/runtime/Cargo.toml` — 无编译错误
- [x] 7.5 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 全绿（384/384）
- [x] 7.6 `./scripts/test-vm.sh` — 全绿（92/92，interp+jit）
- [x] 7.7 更新 `docs/design/ir.md`（ScriptObject/TypeDesc 说明）
- [x] 7.8 更新 `src/runtime/README.md`（types.rs/loader.rs 变更）

## 备注
- `ObjectData` 废弃后只留 `ScriptObject`，不保留兼容名
- 合并 Module 后（main.rs 多 zpkg 场景）也调用 `build_type_registry` — 修复了 `is Shape` 返回 false 的 bug
- 虚拟字段（Str/Array/Map 的 Length/Count）返回 `I64` 与 z42 整型字面量类型一致
