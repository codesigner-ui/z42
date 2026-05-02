# Tasks: 修复 class field 默认初始化

> 状态：🟢 已完成 | 创建：2026-05-02 | 完成：2026-05-02 | 类型：fix（vm 语义变更走完整流程）

## 进度概览
- [x] 阶段 1: P1 TypeChecker — instance field init 绑定
- [x] 阶段 2: P2 Codegen — ctor 注入字段 init + 合成隐式 ctor
- [x] 阶段 3: P3 VM — FieldSlot.type_tag + ObjNew 类型默认值
- [x] 阶段 4: 测试（单元 + golden）
- [x] 阶段 5: 验证 + 文档同步 + 归档

## 阶段 1: P1 TypeChecker
- [x] 1.1 `SemanticModel.cs` — 增加 `IReadOnlyDictionary<FieldDecl, BoundExpr> BoundInstanceInits` 字段 + 构造参数 + 赋值
- [x] 1.2 `TypeChecker.cs` — 新增私有 `Dictionary<FieldDecl, BoundExpr> _boundInstanceInits = new()`
- [x] 1.3 `TypeChecker.cs:404` — 把 `BindStaticFieldInits` 改名为 `BindFieldInits`（同时绑定 instance + static），按 `IsStatic` 写入对应字典
- [x] 1.4 `TypeChecker.cs` — 把 `BindFieldInits` 的产物写入 `BuildSemanticModel` 时传入 `BoundInstanceInits`
- [x] 1.5 调用点位置（构建 SemanticModel 处）从 `BindStaticFieldInits(cu)` 改为 `BindFieldInits(cu)`
- [x] 1.6 实现 Decision 4：`EnsureBaseHasParameterlessCtorOrFail` 静态校验函数（在 IrGen 用，但诊断码常量定义在 `DiagnosticCodes`）
- [x] 1.7 新增诊断码 `Z0922 NeedsExplicitConstructor`

## 阶段 2: P2 Codegen
- [x] 2.1 `FunctionEmitter.cs:EmitMethod` — 在 base ctor call 之后、`EmitBoundBlock(body)` 之前，调用新方法 `EmitInstanceFieldInits(className)`
- [x] 2.2 `FunctionEmitter.cs` — 实现 `EmitInstanceFieldInits(string className)`：遍历 `_ctx.GetClassFields(className)`（如不存在则在 EmitterContext 新增），按声明顺序对有 init 的字段发射 `FieldSetInstr`
- [x] 2.3 `IEmitterContext` 接口 — 暴露 `IReadOnlyList<FieldDecl> GetClassFields(string className)`（按声明顺序，含 init 信息）
- [x] 2.4 `IrGen.cs` — 在每个 class 的方法发射循环结尾增加：
    - 检测 `needSynthCtor = !hasExplicitCtor && hasInstanceInit`
    - 若 needSynthCtor：调 `EnsureBaseHasParameterlessCtorOrFail(cls)`，构造 `FunctionDecl(name=cls.Name, params=[], body=empty, IsStatic=false)`，调 `emitter.EmitMethod(cls.Name, synthCtor, emptyBoundBlock, $"{qualName}.{cls.Name}")`
- [x] 2.5 `MakeSynthImplicitCtor`/`MakeEmptyBoundBlock` 辅助 — 在 IrGen 或 IrGen.Helpers 内
- [x] 2.6 `EmitMethod` 已有 base ctor call 路径处理 `BaseCtorArgs is null` 情况：合成 ctor 没显式 base call → 走默认零参 base call（如 base 有零参 ctor）

## 阶段 3: P3 VM
- [x] 3.1 `src/runtime/src/metadata/types.rs` — `FieldSlot` 增加 `pub type_tag: String`
- [x] 3.2 `src/runtime/src/metadata/types.rs` — 新增 `pub fn default_value_for(type_tag: &str) -> Value`（按 design Decision 5）
- [x] 3.3 `src/runtime/src/metadata/mod.rs` — re-export `default_value_for`
- [x] 3.4 `src/runtime/src/metadata/loader.rs:322` — 构造 `FieldSlot` 时填 `type_tag: f.type_tag.clone()`
- [x] 3.5 `src/runtime/src/corelib/object.rs:27-28` — `FieldSlot { name, type_tag: "str".to_string() }` × 2
- [x] 3.6 grep 整库其他 `FieldSlot { name:` 构造点；全部补 type_tag（编译器会兜底报错，按报错位置补）
- [x] 3.7 `src/runtime/src/interp/exec_instr.rs:305` — `slots = type_desc.fields.iter().map(|f| default_value_for(&f.type_tag)).collect()`
- [x] 3.8 `src/runtime/src/jit/helpers_object.rs:200` — 同样改写
- [x] 3.9 `src/runtime/src/interp/exec_instr.rs` 内 `make_fallback_type_desc` — 检查是否构造空 fields → 不影响（empty Vec），无需改

## 阶段 4: 测试
- [x] 4.1 NEW `src/compiler/z42.Tests/ClassFieldInitTypeCheckTests.cs`：5 个用例（见 design Testing Strategy）
- [x] 4.2 NEW `src/runtime/tests/golden/run/class_field_default_init/source.z42` —— 涵盖 4 个 scenario（A 全 init，B 全无 init，C init+ctor 覆写，P→Q 继承）
- [x] 4.3 NEW `src/runtime/tests/golden/run/class_field_default_init/expected_output.txt`
- [x] 4.4 `./scripts/regen-golden-tests.sh` 生成 `source.zbc`

## 阶段 5: 验证 + 文档 + 归档
- [x] 5.1 `dotnet build src/compiler/z42.slnx` — 无错
- [x] 5.2 `cargo build --manifest-path src/runtime/Cargo.toml` — 无错
- [x] 5.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 全绿（应在原基线 +5 个测试）
- [x] 5.4 `./scripts/test-vm.sh` — 全绿（interp + jit 各 +1 case）
- [x] 5.5 spec scenarios 逐条对应实现位置确认（见验证报告）
- [x] 5.6 文档同步：
    - `docs/design/class.md`（NEW 或 MODIFY）—— 字段默认值规则 + Decision 2/3/4 的 z42 语义偏离声明
    - `docs/design/language-overview.md` —— 在 class 章节加一句"实例字段支持 `=` 初始化器"
    - `docs/design/vm-architecture.md` 或 `runtime` README —— `FieldSlot.type_tag` + `default_value_for` 的实现原理
    - `docs/roadmap.md` —— 在合适分类记录 fix 完成
- [x] 5.7 移动 `spec/changes/fix-class-field-default-init/` → `spec/archive/2026-05-02-fix-class-field-default-init/`
- [x] 5.8 commit + push（自动）—— 包含 `.claude/`、`spec/`、`src/`、`docs/`

## 备注

- 实施时若发现"instance field init scope 是否能引用 this"在 BindIdent 行为上有歧义，先按现有 scope 行为推进；若单元测试发现冲突，记录此处并停下与 User 讨论
- regen-golden-tests.sh 会重生**所有** golden 的 zbc，因此 commit diff 会很大（zbc 是 binary）—— 这是预期行为
- 如果 P3 加 type_tag 后 zbc 序列化格式漂移（已有 zbc 旧版本不匹配 FieldSlot 反序列化），按 workflow"不为旧版本提供兼容"原则直接 regen，不留兼容路径
