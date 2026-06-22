# Tasks: port-z42c-impl-block (replace-csharp S5 prereq)

> 状态：🟡 进行中 | 创建：2026-06-22
> 子系统：`z42c`
> 变更说明：给 z42c 端口 `impl Trait for Type`（跨包 trait 实现）—— parse + bind + IMPL section，
> 与 C# byte-identical。这是 cross-zpkg 用 z42c 编译的前置（→ 删 C# / 移除 dotnet 的最后一块）。
> 原因：replace-csharp S5；cross-zpkg/impl_propagation 用 impl-block，z42c 缺该特性（S2.3 阻塞）。
> 文档影响：self-hosting.md（z42c 支持特性表 + impl-block）。

## 进度概览
- [x] 1. Parser：`impl <Trait> for <Target> { methods }` → ImplDecl AST
- [ ] 2. Binder/SymbolCollector：trait 并入 target 接口集 + methods 并入 target 方法表
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
