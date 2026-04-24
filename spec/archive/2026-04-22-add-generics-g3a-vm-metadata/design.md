# Design: L3-G3a VM 约束元数据 + 加载时校验

## Architecture

```
 TypeChecker                    IrGen                       zbc binary                    VM loader
 ─────────────────────────────────────────────────────────────────────────────────────────────
 _funcConstraints       →   IrFunction               →  SIGS section              →  Function
 _classConstraints          .TypeParamConstraints        per tp: flags+types             .type_param_constraints
                            IrClassDesc                  TYPE section               →  TypeDesc
                            .TypeParamConstraints        per tp: flags+types            .type_param_constraints
                                                                                    +
                                                                                    verify pass:
                                                                                    每 constraint refer
                                                                                    → type_registry
                                                                                    not found & !Std.*
                                                                                    → error
```

## Decisions

### Decision 1: zbc 版本策略

**问题**：格式变更如何处理兼容？

**选项**：
- A. bump minor（0.4 → 0.5），reader 严格匹配版本
- B. flag 位支持新旧共存
- C. 保持版本，老 zbc 丢失约束字段

**决定**：A。
- 仓库里所有 zbc 由脚本（dotnet test, regen-golden-tests.sh, build-stdlib.sh）产生，一次性重生成即可
- 严格版本匹配避免 B 的代码分支和未来调试复杂度
- 新旧不共存对用户透明（z42 仍处于 pre-1.0）

### Decision 2: 格式布局选择

**问题**：约束元数据紧凑编码还是统一 kind list？

**选项**：
- A. 分字段 flag + 可选 base + interface list（紧凑）
  ```
  constraint_flags: u8
  [if HasBaseClass] base_class_name_idx: u32
  interface_count: u8
  interface_name_idx[]: u32 × interface_count
  ```
- B. 统一 kind list
  ```
  constraint_count: u8
  per constraint: kind: u8, name_idx: u32
  ```

**决定**：A。
- 空间紧凑（flag 位打包；typical case 无 base、少量 interface）
- 解析逻辑清晰（无 kind 分派）
- class / struct flag 不占 u32 槽位

### Decision 3: `TypeParamConstraints` 存储位置

**问题**：`IrFunction` / `IrClassDesc` 已有 `TypeParams: List<string>?`。约束挂哪？

**选项**：
- A. 并列字段 `TypeParamConstraints: List<IrConstraintBundle>?`（与 TypeParams 对齐）
- B. TypeParams 改为 `List<IrTypeParamDef(Name, Constraints)>`（破坏性）

**决定**：A。
- 向后兼容，不改 TypeParams 字段类型
- 对齐索引：`TypeParamConstraints[i]` 对应 `TypeParams[i]`
- 空约束时允许 `TypeParamConstraints` 为 null（空 list 也接受）

### Decision 4: Rust 侧 `ConstraintBundle` 结构

参考 C# `GenericConstraintBundle`：

```rust
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, Default)]
pub struct ConstraintBundle {
    #[serde(default)]
    pub requires_class: bool,
    #[serde(default)]
    pub requires_struct: bool,
    #[serde(default)]
    pub base_class: Option<String>,
    #[serde(default)]
    pub interfaces: Vec<String>,
}

impl ConstraintBundle {
    pub fn is_empty(&self) -> bool {
        !self.requires_class && !self.requires_struct
            && self.base_class.is_none() && self.interfaces.is_empty()
    }
}
```

`Function` / `ClassDesc` 新字段：
```rust
#[serde(default)]
pub type_param_constraints: Vec<ConstraintBundle>,  // 长度 = type_params.len()
```

### Decision 5: 加载时 verify pass 的策略

**问题**：何时、如何校验约束引用？

**选项**：
- A. 加载完成后一次性遍历所有 Function + TypeDesc，检查引用
- B. 懒校验：首次使用时才校验
- C. 不校验

**决定**：A，带 Std 命名空间宽松规则：
- 遍历每个带约束的 Function 和 TypeDesc
- 对每个 `interface` / `base_class` 引用：
  - 查 `type_registry`
  - 未找到且名字以 `Std.` 开头 → 放行（lazy loader 会补）
  - 未找到且不以 `Std.` 开头 → `bail!("InvalidConstraintReference: ...")`
- 在 `main.rs` / VM 入口 `Vm::new_with_module` 之后调用一次

**性能**：O(funcs + classes) × O(constraints per type_param)。约束数量通常很少，整体 O(N)，可忽略。

### Decision 6: TSIG section 暂不动

跨 zpkg TypeChecker 消费约束元数据（L3-G3d）单独规划，本次不动 TSIG。编译时跨 zpkg 暂时保持无约束校验（老行为）；本次只打通 zbc 主管道。

### Decision 7: 运行时 Call/ObjNew 校验不做

代码共享策略下 type_args 在运行时不可得，无法在 Call/ObjNew 校验。
- TypeChecker 编译期已覆盖 trusted 场景
- untrusted zbc 的 type_args 校验需要反射或显式 TypeDesc 传递 — 留给 L3-G3b/c

## Implementation Notes

### IrConstraintBundle（C# 侧）

```csharp
// IrModule.cs
public sealed record IrConstraintBundle(
    [property: JsonPropertyName("requires_class")]  bool RequiresClass,
    [property: JsonPropertyName("requires_struct")] bool RequiresStruct,
    [property: JsonPropertyName("base_class")]      string? BaseClass,
    List<string>                                    Interfaces)
{
    public bool IsEmpty => !RequiresClass && !RequiresStruct
                           && BaseClass is null && Interfaces.Count == 0;
}
```

### IrGen 填充逻辑

```csharp
// 在 IrGen 生成 IrFunction / IrClassDesc 时
private List<IrConstraintBundle>? BuildConstraintList(
    string declName,
    IReadOnlyList<string>? typeParams,
    Dictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>> map)
{
    if (typeParams is null || typeParams.Count == 0) return null;
    if (!map.TryGetValue(declName, out var bundles)) return null;
    var result = new List<IrConstraintBundle>(typeParams.Count);
    foreach (var tp in typeParams)
    {
        if (bundles.TryGetValue(tp, out var b))
            result.Add(new IrConstraintBundle(
                b.RequiresClass, b.RequiresStruct,
                b.BaseClass?.Name, b.Interfaces.Select(i => i.Name).ToList()));
        else
            result.Add(new IrConstraintBundle(false, false, null, []));
    }
    return result;
}
```

TypeChecker 侧要暴露 `_funcConstraints` / `_classConstraints` 给 IrGen（之前是私有）。或通过 `SemanticModel` 带出。

### ZbcWriter 编码

```csharp
void WriteConstraintBundle(BinaryWriter w, StringPool pool, IrConstraintBundle b)
{
    byte flags = 0;
    if (b.RequiresClass)  flags |= 0x01;
    if (b.RequiresStruct) flags |= 0x02;
    if (b.BaseClass is not null) flags |= 0x04;
    w.Write(flags);
    if (b.BaseClass is not null)
        w.Write((uint)pool.Idx(b.BaseClass));
    w.Write((byte)b.Interfaces.Count);
    foreach (var i in b.Interfaces)
        w.Write((uint)pool.Idx(i));
}
```

### ZbcReader 解码

对称。读 flag → 条件读 base_class → 读 interface list。

### Rust loader verify pass

```rust
// loader.rs
fn verify_constraints(module: &Module) -> Result<()> {
    for f in &module.functions {
        for b in &f.type_param_constraints {
            check_ref(&b.base_class, &module.type_registry)?;
            for i in &b.interfaces {
                check_ref(&Some(i.clone()), &module.type_registry)?;
            }
        }
    }
    for desc in module.type_registry.values() {
        for b in &desc.type_param_constraints { /* 同 */ }
    }
    Ok(())
}

fn check_ref(name: &Option<String>, registry: &HashMap<String, Arc<TypeDesc>>) -> Result<()> {
    let Some(n) = name else { return Ok(()); };
    if registry.contains_key(n) { return Ok(()); }
    if n.starts_with("Std.") { return Ok(()); } // lazy loader 兜底
    bail!("InvalidConstraintReference: `{n}` not found in type registry");
}
```

## Testing Strategy

### C# 测试（ZbcRoundTripTests.cs）
- `Constraint_Interface_RoundTrip`：单接口约束
- `Constraint_BaseClass_RoundTrip`：基类约束
- `Constraint_ClassFlag_RoundTrip`：class flag
- `Constraint_ClassBaseAndInterface_RoundTrip`：组合

### Rust 测试（src/runtime/tests/metadata/constraint_tests.rs）
- `loader_reads_interface_constraint`：加载 zbc 后 Function.type_param_constraints 非空
- `loader_reads_class_constraint`：TypeDesc.type_param_constraints
- `verify_pass_rejects_unknown_class`：手工构造坏 zbc，verify 报错
- `verify_pass_allows_std_namespace`：Std.* 引用放行

### Golden test
- 不新增 run golden（运行行为不变）
- 现有 68/69/70/71 golden 仍需通过（zbc 格式变更后重生成）

### 验证门
- `dotnet build` + `cargo build` 无错误
- `dotnet test` 全绿（496 + 4 新 round-trip = 500）
- `cargo test` runtime 新增 4 metadata tests 通过
- `./scripts/test-vm.sh` 全绿（全量重生成 zbc 后）
- `./scripts/build-stdlib.sh` stdlib zpkg 重编成功

## Risks

| 风险 | 缓解 |
|------|------|
| 版本 bump 后老 zbc 全 broken | 一次性全量重生成（regen-golden-tests.sh + build-stdlib.sh） |
| IrGen 访问 TypeChecker 私有 dict | 通过 SemanticModel 暴露，或改为 internal 属性 |
| verify pass 对 lazy load 误杀 | Std.* 前缀放行规则（Decision 5） |
| ZpkgReader 跳过 SIGS 时字段偏移计算错 | 按新 spec 严格同步读；加 round-trip test 覆盖 zpkg 路径 |
| 测试 fixture（merge_tests.rs）需更新 | Grep 覆盖 ClassDesc 构造点，补 default 字段 |
