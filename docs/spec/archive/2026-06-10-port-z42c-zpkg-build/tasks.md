# Tasks: port-z42c-zpkg-build — `z42c build` → byte-identical packed `.zpkg`

> 状态：🟢 已完成（User 裁决 A：TSIG/IMPL 留 follow-up port-z42c-tsig）| 创建+归档：2026-06-10 | 子系统锁：z42c（移交 port-z42c-tsig）

## 进度概览
- [x] BP-0 IrGen namespace 限定（类/函数 key/ObjNew 引用 FQ；无 ns 零变化——zbc golden 守护）
- [x] BP-R ZbcWriter 段构建器共享化 refactor（public + 池/remap/allocator 参数；zbc 路径回归零字节漂移）
- [x] BP-1 project SourceDiscovery（glob + Ordinal 排序）
- [x] BP-2 PackageTypes + ZpkgBuilder（exports FQ 幂等 + entry 四级检测 + Sha256 大写 hex + 相对 SourceFile）
- [x] BP-3 ZpkgWriter packed 七段（META/STRS/NSPC/EXPT/DEPS/SIGS/MODS；统一池 + per-module remap/allocator）
- [x] BP-4 driver `build <toml>`（manifest→发现→编译→组装→写 dist/<name>.zpkg）
- [x] BP-5 验证（部分：见实施记录 TSIG 边界）：packed-minimal 同源 golden hex + xtask e2e（build→z42vm 直跑 zpkg + build byte-compare）+ gate 全绿

## BP-0
- [x] 0.1 IrGen：cu.Namespace 前缀类 desc 名/函数 key/exports；ExprEmitter ObjNew class+ctor FQ
- [x] 0.2 验证：既有 zbc golden（empty/f5）+ e2e byte-compare 零变化；新单测 namespaced FQ 断言

## BP-R
- [x] R.1 `_buildFunc/_buildType/_buildSigs/_buildDbug/_buildRegt` → public 静态参数化（函数/类列表+pool+remap+alloc）
- [x] R.2 zbc Write() 改走共享形态；全部 zbc golden 逐字节回归

## BP-1..4（见 design 架构图；每步 30min 粒度在实施时按需细分）
- [x] 1.1 SourceDiscovery.z42（`**/*.z42`+`*.z42`，排除 dist/.cache，Ordinal 排序）+ 单测
- [x] 2.1 PackageTypes.z42 / 2.2 ZpkgBuilder.z42（+entry 检测单测：四级+歧义）/ 2.3 Sha256Hex（string→UTF8 bytes API 核查，缺则 dogfood 流程）
- [x] 3.1 ZpkgWriter.z42 七段 + 装配（zpkg minor 同步 C# 当前值；version-bumping 第 5 步说明扩到 zpkg golden）
- [x] 4.1 driver build 路由 + project/driver toml 依赖边（project+ir、driver+project）
- [x] 5.1 tests/zpkg 单测 ×4 + 5.2 xtask build e2e（直跑）+ 5.3 README×3 + self-hosting.md + gate 全绿（15 units）

## 备注
- C# 权威：ZpkgWriter.WritePacked 的 intern 顺序（zpkg 元串→逐模块[ns/src/hash→InternPoolStrings]）；MODS 体复用 zbc 段构建器（单源防漂移）。
- fixture 实证：SourceFile=项目相对路径；SourceHash=SHA256 大写；EXPT kind func=0；DEPS 可为 0 条；TSIG/IMPL 仅 ExportedModules 非空（MVP 无）。

## 实施记录（2026-06-10）
- 全链落地：`z42c build <toml>` → manifest → SourceDiscovery（glob+Ordinal 排序）→ 逐文件编译 → ZpkgBuilder（ns 去重/exports FQ 幂等/entry 四级检测）→ ZpkgWriterZ packed 七段 → `dist/<name>.zpkg`。
- **e2e 实证**：z42c build 出的 exe zpkg（调用/对象/字段全链 + div-zero oracle）在 z42vm **直接执行**（entry 烤入）✓ gate 常驻步。
- **对真 C# CLI 的逐字节现状**：META/NSPC/EXPT/DEPS 四段 **byte-EQUAL**；STRS/SIGS/MODS 差异**全部源于 TSIG/IMPL 缺位**（真 CLI 恒发导出类型签名段，其串先入池 → 池索引整体移位；SIGS/MODS 同尺寸）。**TSIG/IMPL 本 change 已批 Out of Scope** → spec 场景"同源逐字节"以 follow-up change（port-z42c-tsig：semantics 侧 ExportedTypeExtractor + Tsig 写入器 232 行 C#）补全后达成。
- BP-0 附带修：call 站点限定（自由函数/静态类 FQ；运行期 `undefined function Twice` 抓出）。
- 新 dogfood 坑（→ self-hosting.md 受限写法表）：①z42 无交错数组 `int[][]`——zpkg remap 改为 _buildMods 内按模块重算（Intern 幂等同索引）；②`new T[n]` 不能出现在实参位置（提升局部变量）；③`fn` 是保留字（参数名）。
- C# 侧顺手修复并行 WIP 编译错（SymbolCollector.Classes.cs 调试行 `.ReturnType`→`.Ret`，对方随后自修同形）。
