# Tasks: port-z42c-zpkg-build — `z42c build` → byte-identical packed `.zpkg`

> 状态：⚪ DRAFT 待审批 | 创建：2026-06-10 | 子系统锁：z42c（source-spans 归档后接力）
> **未经 User 批准不动代码（Spec-First gate）。**

## 进度概览
- [ ] BP-0 IrGen namespace 限定（类/函数 key/ObjNew 引用 FQ；无 ns 零变化——zbc golden 守护）
- [ ] BP-R ZbcWriter 段构建器共享化 refactor（public + 池/remap/allocator 参数；zbc 路径回归零字节漂移）
- [ ] BP-1 project SourceDiscovery（glob + Ordinal 排序）
- [ ] BP-2 PackageTypes + ZpkgBuilder（exports FQ 幂等 + entry 四级检测 + Sha256 大写 hex + 相对 SourceFile）
- [ ] BP-3 ZpkgWriter packed 七段（META/STRS/NSPC/EXPT/DEPS/SIGS/MODS；统一池 + per-module remap/allocator）
- [ ] BP-4 driver `build <toml>`（manifest→发现→编译→组装→写 dist/<name>.zpkg）
- [ ] BP-5 验证：packed-minimal 同源 golden hex + xtask e2e（build→z42vm 直跑 zpkg + build byte-compare）+ gate 全绿

## BP-0
- [ ] 0.1 IrGen：cu.Namespace 前缀类 desc 名/函数 key/exports；ExprEmitter ObjNew class+ctor FQ
- [ ] 0.2 验证：既有 zbc golden（empty/f5）+ e2e byte-compare 零变化；新单测 namespaced FQ 断言

## BP-R
- [ ] R.1 `_buildFunc/_buildType/_buildSigs/_buildDbug/_buildRegt` → public 静态参数化（函数/类列表+pool+remap+alloc）
- [ ] R.2 zbc Write() 改走共享形态；全部 zbc golden 逐字节回归

## BP-1..4（见 design 架构图；每步 30min 粒度在实施时按需细分）
- [ ] 1.1 SourceDiscovery.z42（`**/*.z42`+`*.z42`，排除 dist/.cache，Ordinal 排序）+ 单测
- [ ] 2.1 PackageTypes.z42 / 2.2 ZpkgBuilder.z42（+entry 检测单测：四级+歧义）/ 2.3 Sha256Hex（string→UTF8 bytes API 核查，缺则 dogfood 流程）
- [ ] 3.1 ZpkgWriter.z42 七段 + 装配（zpkg minor 同步 C# 当前值；version-bumping 第 5 步说明扩到 zpkg golden）
- [ ] 4.1 driver build 路由 + project/driver toml 依赖边（project+ir、driver+project）
- [ ] 5.1 tests/zpkg golden + 5.2 xtask e2e 两步 + 5.3 README×3 + self-hosting.md + gate 全绿

## 备注
- C# 权威：ZpkgWriter.WritePacked 的 intern 顺序（zpkg 元串→逐模块[ns/src/hash→InternPoolStrings]）；MODS 体复用 zbc 段构建器（单源防漂移）。
- fixture 实证：SourceFile=项目相对路径；SourceHash=SHA256 大写；EXPT kind func=0；DEPS 可为 0 条；TSIG/IMPL 仅 ExportedModules 非空（MVP 无）。
