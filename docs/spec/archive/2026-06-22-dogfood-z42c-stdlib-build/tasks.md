# Tasks: dogfood-z42c-stdlib-build (replace-csharp S3)

> 状态：🟢 已完成 | 完成：2026-06-22 | 创建：2026-06-21
> 子系统：`toolchain`
> 变更说明：生产 stdlib 构建由 z42c 接管（C# 种子 → z42c M2 per-member 重编覆盖）。
> 原因：replace-csharp-compiler S3；C# 全程是种子（不删 C#）。

## ✅ 完成结论（2026-06-22）
`_buildStdlibCore` 翻转为 z42c 接管（① C# 种子 → ② build z42c → ③ run-libs 组装 → ④ z42c interp 重编 stdlib per-member → ⑤ verify + flat view），`_testCompilerZ42Stdlib` 改调 `_csharpBuildStdlibSeed` 避免递归全接管。**full GREEN gate 全绿（z42c-built stdlib 272/272）**：C# 1571 / vm interp 169+jit 165 / cross-zpkg 2 / stdlib 272 / compiler-z42 7/7 byte-identical + 17 units + e2e。dogfood 暴露的全部 8 个 z42c/stdlib bug 均已修复（各独立归档）。

**已修复（独立 commit，z42c bug）**：
1. ✅ **TSIG 可选参数**（fix-z42c-tsig-optional-params，已归档）—— optional param → minArgCount 错。
2. ✅ **multicast aggregate**（fix-z42c-generic-ctor-arity，已归档）—— **根因 = z42c `new C<T>()` 对 arity-overloaded 同名类误取 arity-0 base**（`MulticastException<TResult>` → 抛非泛型 base，catch 失配）。修 `SymbolTable.ResolveTypeP` 优先 `Name$N`。两 aggregate golden 现 MATCH。

**已解（独立归档）**：
3. ✅ **strip-symbols（BLID/.zsym）**：port-z42c-strip-symbols —— z42c 发行构建产 `.zsym` sidecar + BLID；StdlibSidecarPairingTests 过。
4. ✅ **blake3 多块哈希**：fix-blake3-multichunk-root-flag —— strip 的 build_id 暴露 z42.crypto >1024B BLAKE3 错误；修后 z42c.driver build_id == C# nuget 逐字节一致。

5. ✅ **compression `[Native]` named-entry**：fix-z42c-native-named-entry —— z42c 不识别 named `[Native(lib=,entry=)]` → compression extern undefined。修后 18→8。

6. ✅ **cross-ns 静态调用**：fix-z42c-static-call-cross-ns —— Zip→Deflate 误限定当前 ns；修 ExprEmitter 用 QualifyClass。8→4。

**剩余 2 个 codegen bug 全解（2026-06-22，本次收官）**：
7. ✅ **静态字段 mutation 不持久**（fix-z42c-static-field-assign，已归档）—— `_emitAssign` 无 BoundStaticGet LHS 分支 → 静态字段写丢弃。修加 StaticSetInstr 分支。
8. ✅ **ctor `: this(...)` 委托**（fix-z42c-ctor-this-delegation，已归档）—— parser 丢弃 this/base 区分 → `: this(W,S)` 误编为 base ctor 调用 → Bencher() 字段=0。修 MethodDecl 加 IsThisInit + TypeChecker 按位选 TargetCls。
- ⓘ **「blake3 多块 codegen bug」实为回归测试 golden 误写**——已改正，z42c-built blake3 多块 == C#-built，无 codegen bug。

## 进度概览
- [x] 0. 可行性验证（z42c M2 编 22 库 + 272/272 + TSIG bug 修复）
- [x] 1. `_buildStdlibCore` 改 z42c 接管（实现 + build 验证 OK，**已落地**）
- [x] 2. full GREEN gate 全绿（z42c-built stdlib 272/272）
- [x] 3. 文档同步 + 归档

## 1. _buildStdlibCore（scripts/xtask_stdlib.z42）
- [x] 1.1 factor `_csharpBuildStdlibSeed`（现 step-1 C# build 抽出，作种子）
- [x] 1.2 接线：C# 种子 stdlib → `_ensureZ42cTooling`(z42vm+driver) → `_buildCompilerZ42`
- [x] 1.3 run-libs 组装（C#-种子 stdlib + z42c 7 包，copy；复用 `_copyAll`/`_resetDir`）
- [x] 1.4 z42c 重编 stdlib（z42vm + driver.zpkg --mode interp -- build --workspace --release，cwd=src/libraries，Z42_LIBS=run-libs，M2 per-member 覆盖）
- [x] 1.5 verify + flat view 不变（z42c-built）
- [x] 1.6 更新文件头 cold-bootstrap 注释
- [x] 1.7 `_testCompilerZ42Stdlib`（xtask_compiler_z42.z42）改调 `_csharpBuildStdlibSeed`（避免递归全接管）

## 2. 验证
- [x] 2.1 `./xtask build stdlib` → z42c 接管，22 库产出 + flat view
- [x] 2.2 `./xtask test stdlib`（rebuild 路径）→ 272/272（跑在 z42c-built libs）
- [x] 2.3 `./xtask test`（full GREEN）→ 全绿（compiler 1571+vm 169/165+cross-zpkg 2+stdlib 272+compiler-z42 7/7+17 units+e2e）
- [x] 2.4 restore：C# 仍可独立建 stdlib（`_csharpBuildStdlibSeed` 即 C# 路径；铁律种子未断）

## 3. 文档 + 归档
- [x] 3.1 docs/design/compiler/self-hosting.md：生产 stdlib build 由 z42c 接管（S3）
- [x] 3.2 replace-csharp-compiler/tasks.md：S3 勾选 + 措辞（per-member drop-in）
- [x] 3.3 归档 + 释放 toolchain 锁 + commit

## 备注
- bootstrap 序铁律：C# 全程种子；本 change 不删 C#（S5 才删，须先 S4 种子）。
- perf：z42c interp ~30s/build（dogfood 税）；jit 加速留 self-hosting.md Deferred + roadmap 索引。
