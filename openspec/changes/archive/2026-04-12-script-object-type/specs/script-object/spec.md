# Spec: Script Object Type and String Members

## ADDED Requirements

### Requirement: TypeDesc 预构建类型描述符

#### Scenario: 模块加载时构建 TypeDesc 注册表
- **WHEN** `load_artifact(path)` 加载一个包含 ClassDesc 的 module
- **THEN** Loader 为每个 ClassDesc 预构建 `TypeDesc`，存入 `module.type_registry: HashMap<String, Arc<TypeDesc>>`
- **AND** 继承链展平：base 的 field slots 在前，derived 的在后
- **AND** `field_index: HashMap<String, usize>` 已填充（field_name → slot idx）
- **AND** `vtable: Vec<(String, String)>` 已展平（method_name, qualified_func_name），base 方法在前，derived 覆盖同名

#### Scenario: vtable 派生类覆盖基类方法
- **WHEN** 基类定义 `Area()`，派生类也定义 `Area()`
- **THEN** 派生类的 `TypeDesc.vtable` 中 `"Area"` 对应派生类的限定函数名
- **AND** `vtable_index["Area"]` 指向该 slot

### Requirement: ScriptObject 替代 ObjectData

#### Scenario: obj_new 分配 ScriptObject
- **WHEN** VM 执行 `obj_new "Demo.Point" [r1, r2]`
- **THEN** 查 `module.type_registry["Demo.Point"]` 得到 `Arc<TypeDesc>`
- **AND** 分配 `slots: vec![Value::Null; type_desc.fields.len()]`
- **AND** 调用构造函数后，slots 按 field_index 写入

#### Scenario: field_get 按 slot 索引访问
- **WHEN** VM 执行 `field_get %obj, "X"`
- **THEN** `type_desc.field_index["X"]` → slot idx → `slots[idx]`（O(1)，不经 HashMap）

#### Scenario: v_call 按 vtable 派发（O(1)）
- **WHEN** VM 执行 `v_call %obj, "Area" []`
- **THEN** `obj.type_desc.vtable_index["Area"]` → slot → `vtable[slot].1`（qualified func name）
- **AND** 调用该函数，不再走继承链线性扫描

### Requirement: String 虚拟字段

#### Scenario: string.Length 通过 field_get 访问
- **WHEN** z42 代码 `int n = s.Length;`（s 为 string）
- **THEN** 编译器 emit `field_get %s, "Length"`（不再 emit `builtin "__len"`）
- **AND** VM `FieldGet` 对 `Value::Str(s)` 的 `"Length"` 返回 `Value::I32(s.chars().count() as i32)`

#### Scenario: string.IsEmpty（脚本层实现）
- **WHEN** z42 代码 `bool e = s.IsEmpty;`
- **THEN** 调用 `Std.String.IsEmpty` getter（脚本实现：`return Length == 0;`）
- **AND** 返回 `Value::Bool(true/false)`

#### Scenario: Array/List Length 也走 field_get
- **WHEN** z42 代码 `int n = arr.Length;` 或 `list.Count`
- **THEN** 编译器 emit `field_get %arr, "Length"` 或 `field_get %list, "Count"`
- **AND** VM `FieldGet` 对 `Value::Array` 的 `"Length"/"Count"` 返回 `Value::I32(len)`

### Requirement: NativeData 隔离 StringBuilder 内部状态

#### Scenario: StringBuilder 状态在 NativeData 中
- **WHEN** `__sb_append` builtin 被调用
- **THEN** 读写 `ScriptObject.native`（`NativeData::StringBuilder(String)`）
- **AND** `ScriptObject.slots` 中不存在 `__data` 字段

#### Scenario: z42 代码无法访问 StringBuilder 内部 buffer
- **WHEN** z42 代码尝试 `sb.__data`
- **THEN** `FieldGet` 找不到该 slot → 返回 `Value::Null`（或运行时错误）
- **AND** native buffer 不通过 z42 字段暴露

### Requirement: extern 属性语法

#### Scenario: Parser 解析 extern 属性声明
- **WHEN** stdlib 文件包含 `[Native("__str_length")] public extern int Length { get; }`
- **THEN** Parser 正常解析，`{ get; }` 体被跳过（同 `SkipAutoPropBody`）
- **AND** 生成 `FunctionDecl { Name="Length", IsExtern=true, NativeIntrinsic="__str_length" }`

#### Scenario: IrGen 将属性访问路由到 field_get
- **WHEN** IrGen 遇到 `s.Length`（s 为 string 类型）
- **THEN** emit `FieldGetInstr(dst, s_reg, "Length")`（不再走 `builtin "__len"` 特判）

## MODIFIED Requirements

### Length/Count 特判移除
**Before:** IrGenExprs.cs 中 `case MemberExpr m when m.Member is "Length" or "Count"` 直接 emit `builtin "__len"`  
**After:** 移除该特判，`MemberExpr` 统一 emit `field_get`；VM `FieldGet` 处理所有类型的 Length/Count

## IR 映射

```
s.Length       →  field_get %s, "Length"          (Value::Str 虚拟字段)
arr.Length     →  field_get %arr, "Length"         (Value::Array 虚拟字段)
list.Count     →  field_get %list, "Count"         (Value::Array 虚拟字段)
obj.X          →  field_get %obj, "X"              (ScriptObject slot)
obj.Area()     →  v_call %obj, "Area" []           (vtable O(1))
```

## Pipeline Steps

受影响的 pipeline 阶段：
- [x] Parser（extern 属性语法）
- [ ] TypeChecker（不涉及，类型系统不变）
- [x] IR Codegen（IrGen Length/Count 特判移除）
- [x] VM interp（ScriptObject / 虚拟字段 / vtable）
- [x] VM loader（TypeDesc 预构建）
