# Spec: Extended `[Native]` attribute (C6)

## ADDED Requirements

### Requirement: Parser accepts new `[Native(lib=, type=, entry=)]` form

#### Scenario: 全部三个命名参数
- **WHEN** parse `[Native(lib="numz42", type="Counter", entry="inc")]`
- **THEN** 关联的 FunctionDecl 上 `Tier1Binding == ("numz42", "Counter", "inc")`，`NativeIntrinsic == null`

#### Scenario: 顺序无关
- **WHEN** `[Native(entry="x", lib="y", type="z")]`
- **THEN** Tier1Binding 字段映射正确（lib="y", type="z", entry="x"）

#### Scenario: 缺 lib → E0907
- **WHEN** `[Native(type="T", entry="m")]` 缺 lib
- **THEN** 解析报 E0907 NativeAttributeMalformed

#### Scenario: 缺 type → E0907
- **WHEN** `[Native(lib="L", entry="m")]` 缺 type
- **THEN** E0907

#### Scenario: 缺 entry → E0907
- **WHEN** `[Native(lib="L", type="T")]` 缺 entry
- **THEN** E0907

#### Scenario: 未知键 → E0907
- **WHEN** `[Native(lib="L", type="T", entry="m", lulz="z")]`
- **THEN** E0907 message 含 `lulz`

---

### Requirement: 旧形式继续工作

#### Scenario: `[Native("__name")]` 路径不回归
- **WHEN** parse `[Native("__println")]`
- **THEN** `NativeIntrinsic == "__println"`，`Tier1Binding == null`；现有 L1 stdlib 测试继续通过

---

### Requirement: IR Codegen 选择正确的 instr

#### Scenario: Tier1Binding → CallNativeInstr
- **WHEN** 编译 `extern long Foo();` 带 `[Native(lib="L", type="T", entry="e")]`
- **THEN** 生成的 stub function 单 block 含一个 `CallNativeInstr(dst, "L", "T", "e", args)`，**没有** `BuiltinInstr`

#### Scenario: Legacy intrinsic → BuiltinInstr
- **WHEN** 编译 `[Native("__println")] extern void WriteLine(string s);`
- **THEN** stub 含 `BuiltinInstr(dst, "__println", args)`

---

### Requirement: TypeChecker 接受任一形式作 native

#### Scenario: extern + Tier1Binding 通过
- **WHEN** `[Native(lib=...)] public static extern long F(long x);` 给 TypeChecker
- **THEN** 无 E0903/E0904 诊断

#### Scenario: extern 无任一形式 → E0903
- **WHEN** `public static extern long F(long x);`（无 attribute）
- **THEN** E0903 ExternRequiresNative

## IR Mapping

不新增 opcode；复用 C1 已声明、C2 已实现 runtime 的 `CallNativeInstr` (0x53)。

## Pipeline Steps

- [ ] Lexer — 不涉及
- [x] Parser / AST — 加 `Tier1NativeBinding` record + parser 识别命名参数
- [x] TypeChecker — `hasNative` 检查扩展
- [x] IR Codegen — `EmitNativeStub` 二选一
- [ ] VM interp — 不涉及（C2 已 ready）
- [ ] JIT — 不涉及

## Documentation Sync

- `docs/design/error-codes.md` Z0907 从占位 → 已启用
- `docs/design/interop.md` §10 加 C6 行 ✅
- `docs/roadmap.md` C6 → ✅
