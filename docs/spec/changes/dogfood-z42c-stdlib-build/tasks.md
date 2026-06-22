# Tasks: dogfood-z42c-stdlib-build (replace-csharp S3)

> 状态：🔴 BLOCKED（实现+验证可行性，full gate 暴露 z42c parity gap → revert，未落地）| 创建：2026-06-21
> 子系统：`toolchain`（**锁已释放** —— 无代码落地，reverted；阻塞在 z42c 侧）
> 变更说明：生产 stdlib 构建由 z42c 接管（C# 种子 → z42c M2 per-member 重编覆盖）。
> 原因：replace-csharp-compiler S3；C# 全程是种子（不删 C#）。

## 🔴 阻塞结论（2026-06-21；更新 2026-06-22）
S3 实现（`_buildStdlibCore` 改 z42c）+ `build stdlib` 验证成功，但 full GREEN gate（z42c-built stdlib）暴露 z42c parity gap，已 **revert** `_buildStdlibCore` 回 C#（保持 gate 绿）。

**已修复（独立 commit，z42c bug）**：
1. ✅ **TSIG 可选参数**（fix-z42c-tsig-optional-params，已归档）—— optional param → minArgCount 错。
2. ✅ **multicast aggregate**（fix-z42c-generic-ctor-arity，已归档）—— **根因 = z42c `new C<T>()` 对 arity-overloaded 同名类误取 arity-0 base**（`MulticastException<TResult>` → 抛非泛型 base，catch 失配）。修 `SymbolTable.ResolveTypeP` 优先 `Name$N`。两 aggregate golden 现 MATCH。

**已解（独立归档）**：
3. ✅ **strip-symbols（BLID/.zsym）**：port-z42c-strip-symbols —— z42c 发行构建产 `.zsym` sidecar + BLID；StdlibSidecarPairingTests 过。
4. ✅ **blake3 多块哈希**：fix-blake3-multichunk-root-flag —— strip 的 build_id 暴露 z42.crypto >1024B BLAKE3 错误；修后 z42c.driver build_id == C# nuget 逐字节一致。

5. ✅ **compression `[Native]` named-entry**：fix-z42c-native-named-entry —— z42c 不识别 named `[Native(lib=,entry=)]` → compression extern undefined。修后 18→8。

6. ✅ **cross-ns 静态调用**：fix-z42c-static-call-cross-ns —— Zip→Deflate 误限定当前 ns；修 ExprEmitter 用 QualifyClass。8→4。

**剩余阻塞（2 个**新** z42c codegen bug，4 stdlib test，self-hosting-future-s3-remaining-codegen-bugs）**：full gate 其余全绿（C# 1571 含 sidecar+multicast / vm goldens 334 / cross-zpkg 2 / ~268/272 stdlib）。
- 🔴 **blake3 多块 z42c codegen**（1×）：z42c miscompiles 多块树代码（源 fix 正确，C#-built 过；疑 `cvList=next` 重赋参数数组 / `(p/2)*8` 索引）
- 🔴 **静态字段 mutation 不持久**（3× diagnostics）：`SetMinLevel(3)` 后 `GetMinLevel()` 返默认 2
- **前置**：2 bug 全解后翻转 `_buildStdlibCore` 重跑 full gate → S3 完成。

## 进度概览
- [x] 0. 可行性验证（z42c M2 编 22 库 + 272/272 + TSIG bug 修复）
- [~] 1. `_buildStdlibCore` 改 z42c 接管（实现+build 验证 OK；**reverted**——阻塞）
- [ ] 2. full GREEN gate 全绿 —— 🔴 阻塞（BLID sidecar + multicast aggregate）
- [ ] 3. 文档同步 + 归档 —— 待解阻塞

## 1. _buildStdlibCore（scripts/xtask_stdlib.z42）
- [ ] 1.1 factor `_csharpBuildStdlibWorkspace`（现 step-1 C# build 抽出，作种子）
- [ ] 1.2 接线：C# 种子 stdlib → `_buildCompilerZ42` → `_buildRuntime`(ensure z42vm)
- [ ] 1.3 run-libs 组装（C#-种子 stdlib + z42c 7 包，copy；复用 `_copyAll`/`_resetDir`）
- [ ] 1.4 z42c 重编 stdlib（z42vm + driver.zpkg --mode interp -- build --workspace --release，cwd=src/libraries，Z42_LIBS=run-libs，M2 per-member 覆盖）
- [ ] 1.5 verify + flat view 不变（z42c-built）
- [ ] 1.6 更新文件头 cold-bootstrap 注释

## 2. 验证
- [ ] 2.1 `./xtask build stdlib` → z42c 接管，22 库产出 + flat view
- [ ] 2.2 `./xtask test stdlib`（rebuild 路径）→ 272/272（跑在 z42c-built libs）
- [ ] 2.3 `./xtask test`（full GREEN）→ 全绿（compiler+vm+cross-zpkg+stdlib+compiler-z42）
- [ ] 2.4 restore：确认 C# 仍可独立建 stdlib（铁律：种子未断）

## 3. 文档 + 归档
- [ ] 3.1 docs/design/compiler/self-hosting.md：生产 stdlib build 由 z42c 接管（S3）
- [ ] 3.2 replace-csharp-compiler/tasks.md：S3.1/S3.2 勾选 + 措辞（per-member drop-in）
- [ ] 3.3 归档 + 释放 toolchain 锁 + commit

## 备注
- bootstrap 序铁律：C# 全程种子；本 change 不删 C#（S5 才删，须先 S4 种子）。
- perf：z42c interp ~30s/build（dogfood 税）；jit 加速留 self-hosting.md Deferred + roadmap 索引。
