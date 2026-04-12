# Design: Script Object Type and String Members

## Architecture

```
IR (ClassDesc: name, base, fields[])
        │  加载时
        ▼
TypeDesc Registry (HashMap<String, Arc<TypeDesc>>)
  ├── TypeDesc { name, base_name, fields[], field_index{}, vtable[], vtable_index{} }
  └── ...

ScriptObject (replaces ObjectData)
  ├── type_desc: Arc<TypeDesc>   ← 类型身份（CoreCLR MethodTable*)
  ├── slots: Vec<Value>          ← 字段按 slot 索引（CoreCLR 对象内存布局）
  └── native: NativeData         ← native backing（CoreCLR string/array 内联数据）

Value::Str(String)               ← 字符串基元（不装箱）
  → FieldGet("Length")  →  i32  ← 虚拟字段派发
  → FieldGet("Count")   →  i32
Value::Array(...)
  → FieldGet("Length")  →  i32
  → FieldGet("Count")   →  i32
```

## Decisions

### Decision 1: TypeDesc 在 Module 中的位置
**问题：** TypeDesc 注册表放在 Module 还是独立结构？  
**选项：**  
  A — 放在 `Module.type_registry: HashMap<String, Arc<TypeDesc>>`：加载后 Module 自包含，Interp 直接查  
  B — 独立全局注册表（thread_local）：支持跨 Module 查询  
**决定：** 选 A，Phase 1 单 Module 执行，Module 自包含更简洁；多 Module 场景 L3 再扩展

### Decision 2: vtable 存储格式
**问题：** vtable 用 HashMap 还是 Vec + index？  
**选项：**  
  A — `HashMap<String, String>`：简单，O(1) 查，但无整数 slot（JIT 无法直接用）  
  B — `Vec<(String, String)>` + `HashMap<String, usize>`：保留 slot index，为 JIT 预留  
**决定：** 选 B（用户确认），vtable 整数 slot 为 JIT 阶段的直接派发保留语义

### Decision 3: Value::Str 虚拟字段 vs 装箱
**问题：** string.Length 需要 boxing 吗？  
**选项：**  
  A — 装箱 Value::Str → Value::Object with NativeData::StringBuffer：纯 OOP，重  
  B — VM FieldGet 对 Value::Str 做虚拟派发：零开销，解释器阶段足够  
**决定：** 选 B，`Value::Str` 保持基元；JIT 阶段可以特化为直接字段访问

### Decision 4: IsEmpty 在 VM 层还是脚本层
**问题：** `s.IsEmpty` 需要 VM 特判吗？  
**决定：** 脚本层（`String.z42` 中 `public bool IsEmpty { get { return Length == 0; } }`）；  
  VM 不需要感知 IsEmpty，完全由 z42 代码实现，符合"能在脚本实现的就在脚本实现"原则

## Implementation Notes

### TypeDesc 构建算法（loader.rs）

```
fn build_type_registry(module: &mut Module) {
    // 拓扑排序：base 先于 derived
    let order = topo_sort(&module.classes);
    let mut registry = HashMap::new();

    for class_name in order {
        let desc = module.classes[class_name];
        let base_td: Option<Arc<TypeDesc>> = desc.base_class
            .and_then(|b| registry.get(b).cloned());

        // 展平 fields：先 base slots，再 derived slots
        let mut fields: Vec<FieldSlot> = base_td.as_ref()
            .map(|b| b.fields.clone())
            .unwrap_or_default();
        for f in &desc.fields {
            if !fields.iter().any(|s| s.name == f.name) {
                fields.push(FieldSlot { name: f.name.clone() });
            }
        }
        let field_index = fields.iter().enumerate()
            .map(|(i, f)| (f.name.clone(), i))
            .collect();

        // 展平 vtable：先 base，再 derived 覆盖
        let mut vtable: Vec<(String, String)> = base_td.as_ref()
            .map(|b| b.vtable.clone())
            .unwrap_or_default();
        let mut vtable_index: HashMap<String, usize> = base_td.as_ref()
            .map(|b| b.vtable_index.clone())
            .unwrap_or_default();
        for func in &module.functions {
            if func.name.starts_with(&format!("{}.", class_name)) {
                let method = func.name[class_name.len()+1..].to_string();
                if let Some(&slot) = vtable_index.get(&method) {
                    vtable[slot] = (method, func.name.clone());
                } else {
                    let slot = vtable.len();
                    vtable_index.insert(method.clone(), slot);
                    vtable.push((method, func.name.clone()));
                }
            }
        }

        registry.insert(class_name.to_string(), Arc::new(TypeDesc {
            name: class_name.to_string(),
            base_name: desc.base_class.clone(),
            fields, field_index, vtable, vtable_index,
        }));
    }
    module.type_registry = registry;
}
```

### FieldGet 虚拟字段派发（interp/mod.rs）

```rust
Instruction::FieldGet { dst, obj, field_name } => {
    let val = match frame.get(*obj)? {
        Value::Object(rc) => {
            let obj = rc.borrow();
            let idx = obj.type_desc.field_index.get(field_name)
                .copied()
                .ok_or_else(|| anyhow!("no field `{field_name}` on `{}`", obj.type_desc.name))?;
            obj.slots[idx].clone()
        }
        Value::Str(s) => match field_name.as_str() {
            "Length" => Value::I32(s.chars().count() as i32),
            _ => bail!("string has no field `{field_name}`"),
        },
        Value::Array(rc) => match field_name.as_str() {
            "Length" | "Count" => Value::I32(rc.borrow().len() as i32),
            _ => bail!("array has no field `{field_name}`"),
        },
        other => bail!("FieldGet: not an object or known value type: {:?}", other),
    };
    frame.set(*dst, val);
}
```

### Parser 变更（TopLevelParser.cs）

`ParseFunctionDecl` 中，`isExtern == true` 时，在检查 `;` 之后增加 `{` 的处理：

```csharp
if ((isAbstract || isExtern) && cursor.Current.Kind == TokenKind.Semicolon) {
    cursor = cursor.Advance();
    body = new BlockStmt([], start);
} else if (isExtern && cursor.Current.Kind == TokenKind.LBrace) {
    // extern property: { get; } or { get; set; }
    SkipAutoPropBody(ref cursor);
    body = new BlockStmt([], start);
} else if (...) { ... }
```

## Testing Strategy

- **单元测试**（IrGenTests.cs）：`s.Length` 生成 `FieldGetInstr("Length")`，不生成 `BuiltinInstr("__len")`
- **Golden test**：新增 `string_members.z42` 示例，覆盖 `Length`、`IsEmpty`
- **VM 集成测试**（test-vm.sh）：现有所有 object 测试必须仍然全绿
- **StringBuilder 测试**：确认 `__sb_append`/`__sb_to_string` 行为不变
