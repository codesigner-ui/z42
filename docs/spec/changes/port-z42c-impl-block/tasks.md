# Tasks: port-z42c-impl-block (replace-csharp S5 prereq)

> 状态：🟡 进行中 | 创建：2026-06-22
> 子系统：`z42c`
> 变更说明：给 z42c 端口 `impl Trait for Type`（跨包 trait 实现）—— parse + bind + IMPL section，
> 与 C# byte-identical。这是 cross-zpkg 用 z42c 编译的前置（→ 删 C# / 移除 dotnet 的最后一块）。
> 原因：replace-csharp S5；cross-zpkg/impl_propagation 用 impl-block，z42c 缺该特性（S2.3 阻塞）。
> 文档影响：self-hosting.md（z42c 支持特性表 + impl-block）。

## 进度概览
- [x] 1. Parser：`impl <Trait> for <Target> { methods }` → ImplDecl AST
- [~] 2. Binder ✅ in-package：2a 合并 + 2b TypeChecker 绑体 + 2c IrGen 发 `<qualTarget>.<m>`（_qClass 跨包 ns 解析）。单包 impl 端到端跑通（oracle exit 0）。剩 3 IMPL section（跨包）
- [ ] 3. IMPL section 发射（z42c.ir，镜像 C# 格式）+ 方法体进 MODS（func 名 `<Target>.<m>`）
- [ ] 4. byte-identical：z42c-built impl_propagation == C#-built（逐字节，ignore BLID）
- [ ] 5. extern-in-impl 校验（C# 禁止）+ 文档 + 归档

## 1. Parser ✅
- [x] `ImplDecl` AST（Decl.z42）：TraitType / TargetType / Methods[] / count。镜像 C# ImplDecl。
- [x] `_parseImplDecl`（Parser.z42）：consume impl → trait 类型 → for → target 类型 → 成员块（`_parseMember` owner=target 简名）。dispatch 在顶层 decl 循环加 `TokenKind.Impl` 分支。
- [x] 验证：z42c `--emit-zbc` 一个含 `impl IG for R {...}` 的文件 → 无 parse error，产 zbc（impl 当前下游静默跳过，binder/codegen 待做）。
- 踩坑：`trait` 是 z42 保留关键字 → 变量/参数名用 `traitTy`（非 `trait`）。

## 备注
- z42c 自身 + stdlib 不用 impl-block → parser-only 对现有构建惰性（无 ImplDecl 节点），安全增量提交。
- byte-identical 本地可验（z42c-built vs C#-built impl_propagation 逐字节），不需 CI。

## 3. IMPL section（跨包可见性，byte-identical）— 实现规范

**现状**：z42c `_buildImpl` 发**空** IMPL（每模块 0 条，匹配无-impl 包，gate 绿）；ZpkgReader **不读** IMPL（Phase3 延后）。step 3 = 填充 emit + 加 read。

**IMPL section 字节布局（镜像 C# BuildImplSection，必须逐字节一致）**：
```
u16 moduleCount
per module:
  u32 namespace(poolIdx)
  u16 implCount
  per impl:
    u32 targetFqName(poolIdx)   # 如 Demo.Target.Robot
    u32 traitFqName(poolIdx)    # 如 Demo.Greeter.IGreet（FQ）
    u8  traitTypeArgsCount; per arg: u32(poolIdx)
    u16 methodCount
    per method (WriteMethodDef):
      u32 name(poolIdx); u32 returnType(poolIdx); u32 visibility(poolIdx)
      u8 flags(0x01 static|0x02 virtual|0x04 abstract)
      u16 minArgCount; u8 paramCount
      per param: u32 name(poolIdx); u32 typeName(poolIdx)
```
intern 时机：C# InternZpkgStrings 在 deps 之后、逐模块前 intern impl 串（target/trait/args/method 串）——z42c `_internTsig` 同处补 impl 串。

**5 个组件**：
- A. 数据模型：`ExportedImplZ`（TargetFq/TraitFq/TypeArgs[]/Methods[ExportedMethodZ]）+ `ExportedModuleZ.Impls[]`（PackageTypes.z42）。
- B. 提取：ExportedTypeExtractor 收 ImplDecl → ExportedImplZ（target/trait 经 QualifyClass FQ 化；methods 经 _requiredCount 算 minArgCount）。
- C. intern：`_internTsig` 补 impl 串（顺序 1:1 C# InternZpkgStrings impl 块）。
- D. emit：`_buildImpl` 用 Exported[mi].Impls 填充（替换 WriteU16(0)）。
- E. read+apply：ZpkgReader 读 IMPL → ImportedSymbols.impls；ImportedSymbolLoader/SymbolCollector 消费端把 trait 并入 imported target 的接口集 + methods 并入其方法表（使 main 包 `r.Hello()` 解析）。

**验证**：z42c build impl_propagation 三包（target/ext/main）逐字节 == C# build（ignore BLID）；+ main 跑通 `r.Hello()`（oracle）。
**踩坑**：`trait`/`impl` 是 z42 保留字 → 变量用 traitTy/implD。方法体 func 名 `_qClass(target).<m>`（imported target 经 ImportedClassNs 解析到声明 ns）。
