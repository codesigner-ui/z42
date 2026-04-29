# Spec: Class-level `[Native]` defaults (C9)

## ADDED Requirements

### Requirement: Tier1NativeBinding fields are nullable to permit partial info

#### Scenario: Method full form populates all three
- **WHEN** `[Native(lib="L", type="T", entry="m")]` parsed
- **THEN** `FunctionDecl.Tier1Binding = (Lib="L", TypeName="T", Entry="m")` (no nulls)

#### Scenario: Method entry-only form has nulls
- **WHEN** `[Native(entry="m")]` parsed alone
- **THEN** `Tier1Binding = (Lib=null, TypeName=null, Entry="m")`

---

### Requirement: Class-level [Native] populates ClassNativeDefaults

#### Scenario: lib + type 仅
- **WHEN**
  ```z42
  [Native(lib="numz42", type="Counter")]
  public static class NumZ42 { }
  ```
- **THEN** `ClassDecl.ClassNativeDefaults = (Lib="numz42", TypeName="Counter", Entry=null)`

#### Scenario: 类级仅 entry → E0907
- **WHEN** `[Native(entry="x")] class C { ... }`
- **THEN** parser 或 typecheck 报 E0907 含 "class-level [Native] requires lib + type"

---

### Requirement: TypeChecker stitches and validates final binding

#### Scenario: 拼接成功 — class defaults + method entry
- **WHEN**
  ```z42
  [Native(lib="L", type="T")]
  class C {
      [Native(entry="m")]
      public static extern long F();
  }
  ```
- **THEN** 无诊断；IR codegen emit `CallNativeInstr(dst, "L", "T", "m", args)`

#### Scenario: method 缺 entry 且 class 也无 → E0907
- **WHEN** `[Native(lib="L", type="T")] class C { [Native()] extern long F(); }` (or method without [Native])
- **THEN** E0907 "stitched binding incomplete: missing entry"

#### Scenario: method 全形式 + 无 class defaults — C6 路径不回归
- **WHEN** `class C { [Native(lib="L", type="T", entry="m")] extern long F(); }`
- **THEN** 无诊断；IR含 `CallNativeInstr(dst, "L", "T", "m", args)`

#### Scenario: method 覆盖 class lib
- **WHEN** class defaults `(L1, T1)`，method `[Native(lib="L2", entry="m")]`
- **THEN** stitched = `(L2, T1, "m")`（method 字段覆盖 class）

---

### Requirement: IR Codegen emits CallNativeInstr with stitched binding

#### Scenario: stitched 三字段全嵌入 IR
- **WHEN** 编译 stitched-form 类
- **THEN** 生成的 stub function 单 block 含 `CallNativeInstr` whose `Module/TypeName/Symbol` 字段对应 stitched 结果

## IR Mapping

不新增 opcode；`CallNativeInstr` (0x53) 行为不变 —— 字段值由 stitched 计算决定。

## Pipeline Steps

- [x] Parser — 接受 partial Tier1NativeBinding，兼容 class-level placement
- [x] TypeChecker — 拼接 + 校验
- [x] IR Codegen — emit stitched binding
- [ ] VM — 不涉及（C2 dispatch 不变）

## Documentation Sync

- `docs/design/error-codes.md` E0907 加 "stitched binding incomplete" 抛出条件
- `docs/design/interop.md` §10 加 C9 行 ✅
- `docs/roadmap.md` C9 → ✅
