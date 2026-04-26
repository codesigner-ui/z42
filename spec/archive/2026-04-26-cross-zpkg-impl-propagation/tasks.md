# Tasks: cross-zpkg-impl-propagation (L3-Impl2)

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：ir (zbc IMPL section)

## 进度概览
- [x] 阶段 1: ExportedImplDef + IMPL section 序列化 (Writer + Reader)
- [x] 阶段 2: ExportedTypeExtractor.ExtractImpls
- [x] 阶段 3: ImportedSymbolLoader Phase 3 — impl merge
- [x] 阶段 4: IrGen QualifyClassName fix (line 132 + EmitMethod)
- [x] 阶段 5: VM decoder 跳过 IMPL section (天然跳过 — 基于 tag 查找)
- [x] 阶段 6: 单元测试 (IMPL roundtrip + ImportedSymbolLoader Phase 3)
- [x] 阶段 7: 文档同步 + 归档

---

## 阶段 1: ExportedImplDef + IMPL section

- [x] 1.1 `ExportedTypes.cs` 加 `ExportedImplDef(TargetFqName, TraitFqName, TraitTypeArgs, Methods)`
- [x] 1.2 `ExportedModule` 加字段 `List<ExportedImplDef>? Impls`
- [x] 1.3 `ZbcWriter.cs` version 0.7 → 0.8（VersionMinor），changelog 更新
- [x] 1.4 `ZpkgWriter.cs` 写 IMPL section（tag `"IMPL"u8`）+ InternTsigStrings 处理 impl 字符串
- [x] 1.5 `ZpkgReader.cs` 解码 IMPL section → 填充 `ExportedModule.Impls`

## 阶段 2: ExportedTypeExtractor.ExtractImpls

- [x] 2.1 实现 `ExtractImpls(SemanticModel sem, CompilationUnit cu, string ns)` —
  从 cu.Impls 抽出每条，FQ 化 target/trait 名（imported 用源命名空间，local 用本包）
- [x] 2.2 加 `ExtractTypeName` / `TypeExprToString` helpers
- [x] 2.3 集成到 `Extract()` 主入口

## 阶段 3: ImportedSymbolLoader Phase 3

- [x] 3.1 `MergeImpls(...)` 私有方法 + `SplitFqName` helper
- [x] 3.2 `Load()` 末尾调用 MergeImpls
- [x] 3.3 first-wins：方法名 TryAdd；trait 列表去重；namespace 匹配验证

## 阶段 4: IrGen QualifyClassName fix

- [x] 4.1 `IrGen.cs` line 132: `QualifyName(targetNt.Name)` → `((IEmitterContext)this).QualifyClassName(...)`
- [x] 4.2 `EmitMethod` 内部 qualClass 同步改用 QualifyClassName
- [x] 4.3 86_extern_impl_user_class 仍通过（local target 行为不变）

## 阶段 5: VM decoder skip IMPL

- [x] 5.1 VM 基于 tag-lookup 查询 sections，未识别 section 天然跳过 — 无需改动
- [x] 5.2 zbc 解码器不强制版本 — 0.8 zbc 自动接受

## 阶段 6: 单元测试

- [x] 6.1 `CrossZpkgImplTests.cs` IMPL section roundtrip：3 个测试（基础 / TraitTypeArgs / 空 impls）
- [x] 6.2 `ImportedSymbolLoaderTests.cs` Phase 3 merge：3 个测试（method 合并 + trait 注册 / target 缺失 silent skip / 同名冲突 first-wins）
- [x] 6.3 `regen-golden-tests.sh` 重生所有 zbc（version bump）
- [x] 6.4 全量回归：dotnet test 592/592 / test-vm 188/188 (94 interp + 94 jit) / cargo 61/61 / stdlib 5/5

## 阶段 7: 文档 + 归档

- [x] 7.1 `docs/design/compiler-architecture.md` 加 "跨 zpkg impl 块传播" 小节
- [x] 7.2 `docs/design/generics.md` extern impl 章节加 L3-Impl2 落地小节
- [x] 7.3 `docs/roadmap.md` L3-Impl2 状态 ✅
- [x] 7.4 tasks.md 状态 → `🟢 已完成`
- [x] 7.5 归档 + commit + push（scope `feat(ir+typecheck)`）

## 备注

### 实施过程关键发现

1. **ZpkgReader IMPL 模块匹配必须 positional**（不能按 namespace）：
   z42.core 含多个 .z42 文件，TSIG/IMPL 都按文件输出独立 module，但所有 module
   共享 namespace `Std`。最初用 `modules.ToDictionary(m => m.Namespace, ...)`
   导致 `ArgumentException: duplicate key`，stdlib 编译时 Phase 3 异常 →
   imported Exception 等 class 的 ImportedClassNames 集合被破坏 → consumer
   QualifyClassName 走错分支，IR 生成 `@Exception` 而非 `@Std.Exception`。
   修复：IMPL section 按 TSIG 模块顺序 positional 匹配。

2. **零 VM 改动**：IMPL section 仅承载声明级元数据。方法 body 走 MODS section
   注册到 `func_index`，VM 通过 `{ClassFq}.{Method}` 名字解析自动找到 impl 提供
   方的 zpkg 函数。VM 解码器基于 tag-lookup 天然跳过未识别 section。

3. **Out of scope**：真·端到端跨 zpkg golden test 需要 multi-zpkg 测试基础设施
   （目前 `test-vm.sh` 只支持 single source.z42 → source.zbc）。单元测试覆盖
   wire format roundtrip + Phase 3 merge 逻辑；端到端验证将随 z42.numerics 等
   实际扩展包出现自动落地。

### 解锁

z42.numerics 等扩展包能给 z42.core 的 primitive 类型追加数值 trait；
任何"扩展包给 z42.core 类型加新接口"模式（dependency injection 风）
都已具备机制基础。
