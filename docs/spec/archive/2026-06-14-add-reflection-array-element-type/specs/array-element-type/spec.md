# Spec: 数组元素类型反射（运行期不擦除）

## ADDED Requirements

### Requirement: 数组类型暴露 IsArray + 元素类型（全来源一致，不擦除）

#### Scenario: typeof(T[])
- **WHEN** `typeof(int[]).IsArray` / `.GetElementType()`
- **THEN** `true` / `typeof(int)`（`.Name=="int"`）

#### Scenario: 运行期数组值 GetType（关键——不擦除）
- **WHEN** `int[] arr = new int[3]; arr.GetType().GetElementType()`
- **THEN** 返回 `typeof(int)`（`.Name=="int"`）——数组值运行期携带元素类型；`arr.GetType().IsArray==true`；`Name=="Array"`/`FullName=="Std.Array"` 不变

#### Scenario: 用户类数组
- **WHEN** `class Foo {}` 后 `typeof(Foo[]).GetElementType()` / `(new Foo[1]).GetType().GetElementType()`
- **THEN** 均返回 `Foo` 的真句柄 Type（`.Name=="Foo"`）

#### Scenario: 数组字段 / 参数
- **WHEN** 字段 `public int[] data;` → `f.FieldType`
- **THEN** `f.FieldType.IsArray==true` / `.GetElementType().Name=="int"`

#### Scenario: 交错数组
- **WHEN** `typeof(int[][]).GetElementType()`
- **THEN** `int[]` 的 array Type（其 `.GetElementType().Name=="int"`）

#### Scenario: 非数组
- **WHEN** `typeof(int).IsArray` / `typeof(SomeClass).GetElementType()`
- **THEN** `false` / `null`

#### Scenario: 空数组也带元素类型
- **WHEN** `int[] e = new int[0]; e.GetType().GetElementType()`
- **THEN** `typeof(int)`（element_type 与 size 无关）

## MODIFIED Requirements

### Requirement: 数组堆表示（运行期类型信息）
**Before:** `Value::Array(GcRef<Vec<Value>>)` — 纯元素，类型擦除。
**After:** `GcRef<ArrayObj>`，`ArrayObj { element_type: Arc<str>, elems: Vec<Value> }`。`Deref`→`elems` 保多数消费点不变。

### Requirement: ArrayNew / ArrayNewLit wire
**Before:** `ArrayNew{dst,size,elem_tag}` / `ArrayNewLit{dst,elems}`。
**After:** 各加 `elem_type_idx: u32`（string-pool，元素 FQ 名）。zbc 1.15→1.16 / zpkg 0.17→0.18。

### Requirement: typeof(数组) 运行期类型名
**Before:** emit `"Std.Array"`。**After:** emit `<elem>[]`。`make_type_from_name` 认 `[]`。

## IR Mapping

`ArrayNew`/`ArrayNewLit` 新增 `elem_type_idx`（zbc 格式变更，1.16）。`typeof` 仍 `ConstStr+__typeof`（仅常量串变）。`Type.GetElementType()` → `__type_element` builtin 读 TypeDesc.array_element。

## Pipeline Steps

- [ ] 二进制格式（ZbcWriter/Reader + bytecode.rs + version + fixtures + z42c writer）
- [ ] VM（ArrayObj + GC + exec_array + JIT + reflection.rs + object.rs）
- [ ] Compiler（IrGen ArrayNew/Lit 元素类型 emit + VisitTypeof）
- [ ] stdlib（Type.IsArray + GetElementType）
