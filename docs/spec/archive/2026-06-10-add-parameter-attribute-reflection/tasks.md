# Tasks: 参数级用户 attribute 反射

> 状态：🟢 已完成 | 创建：2026-06-10 | 完成：2026-06-10 | 类型：ir（完整流程）

## 验证报告（2026-06-10）

- ✅ **dotnet GoldenTests：1556/1556**（含新 `src/tests/types/param_attributes.z42` full VM e2e：parse `[Tag] int p` → synth factory → SIGS per-param wire → runtime `FunctionCold.param_attributes` → `ParameterInfo.GetCustomAttributes()` 活实例 + `GetAttribute(Type)`，实例方法 `this` 偏移正确）
- ✅ **cargo test --lib：757 + 21**（含 zbc_reader version-pin 测试更新到 1.15/0.17）
- ✅ **格式 round-trip：Zbc/Zpkg FormatGoldenTests 78/78**（ByteEqual fixture regen ×2；ZbcWriter/Reader + ZpkgWriter/Reader 双向对齐——发现并修复 packed-zpkg SIGS 读/写两侧遗漏：ZpkgWriter.Sections + ZpkgReader.Sections）
- ✅ **stdlib regen：22/22 @ 0.17**（clean rebuild；nuke 缓存破 bootstrap 循环）
- ✅ **z42c writer 同步**：ZbcFormat.z42 1.15 + ZbcWriter.z42 SIGS per-param 镜像 + ZpkgWriter.z42 0.17 + zbc_tests.z42 golden（empty hex 仅版本字节 0e→0f，因 Main 无参；逻辑健全）
- ⚠️ **xtask GREEN gate 不可跑** —— 阻塞于**pre-existing 多文件 project-build 命名空间双重限定 bug**（`<Ns>.<Ns>.fn` / `Z42Xtask.Std.Int32.Parse`），零-attribute 最小项目即复现 → 证非本 change 回归（synthesizer 对无 attr CU 是 no-op）。format bump 强制 regen 才暴露；上次 xtask regen 在 zbc 1.14，中间提交（疑 port-z42c-tsig 系列）引入。**User 裁决（2026-06-10）：commit 本 change（dotnet 权威门全绿），merge bug 单独 fix change 跟踪**（memory `reference_multifile_project_namespace_double_qualify_bug`）。

> **类型：ir（完整流程）**

> **Decision 1 裁决（2026-06-10）**：User 在 gate 选「implement now, defer z42c」；但裁决同时 **port-z42c-tsig 恰好归档释放 z42c** → 升级为更优路径：本变更**共占 z42c**，clean full impl（含 z42c writer 同步），一次 GREEN commit，不留红门。阶段 5 不再延后。

## 进度概览
- [ ] 阶段 0: z42c 锁排程裁决（Decision 1，6.5 gate）
- [ ] 阶段 1: 编译器前端（parser + IR + synthesizer）
- [ ] 阶段 2: 二进制格式 bump（C# writer/reader + 版本 + fixtures）
- [ ] 阶段 3: runtime（reader + loader + reflection builtin）
- [ ] 阶段 4: stdlib（ParameterInfo API）
- [ ] 阶段 5: z42c writer 同步（待 Decision 1 排程）
- [ ] 阶段 6: 测试 + 文档 + 验证

## 阶段 0: 排程（gate）
- [ ] 0.1 6.5 gate：User 裁决 Decision 1（A 等 port 归档 / B 共占 z42c / C 拆分）

## 阶段 1: 编译器前端
- [ ] 1.1 `Ast.cs`：`Param` record 增 `Attributes`（`List<UserAttribute>?`）
- [ ] 1.2 `TopLevelParser.Members.cs`：参数解析捕获 leading `[Attr]`（镜像 field 路径）；插桩确认 ParamCount 是否含 this
- [ ] 1.3 `IrModule.cs`：`IrFunction.ParamAttributes`（`List<List<IrAttributeRef>>?`）
- [ ] 1.4 `AttributeFactorySynthesizer.cs`：合成 `__attr$param$<func>$<idx>$<n>` factory
- [ ] 1.5 `IrGen.*.cs`：EmitFunction 填充 `ParamAttributes`（源码参数维度）

## 阶段 2: 二进制格式
- [ ] 2.1 `ZbcWriter.cs`：`VersionMinor` 14→15（+注释）；SIGS 每函数尾写 per-param 块；`InternAttributeRefs` 覆盖 param attr 串
- [ ] 2.2 `ZbcReader.cs`：SIGS 读 per-param 块
- [ ] 2.3 `ZpkgWriter.cs`：`VersionMinor` 16→17（+注释内嵌 zbc 版本）
- [ ] 2.4 `docs/design/runtime/zbc.md` + `zpkg.md`：Minor changelog 各 +1 行
- [ ] 2.5 `generate-fixtures.sh` ×2 regen；`dotnet test --filter Zbc|Zpkg` 绿

## 阶段 3: runtime
- [ ] 3.1 `bytecode.rs`：func sig 增 per-param `attributes`
- [ ] 3.2 `zbc_reader.rs`：`ZBC_VERSION_MINOR=15` / `ZPKG_VERSION_MINOR=17`；SIGS 读 per-param attr（复用 `read_attr_refs`）；更新 version-pin 单测
- [ ] 3.3 `loader.rs`：建 `(qualifiedFunc, paramIdx) → attr refs` 索引
- [ ] 3.4 `types.rs`：承载 param-attr 索引（func 冷区）
- [ ] 3.5 `reflection.rs`：`ParameterInfo` 加 `__qualified`/`__position`；新 `__param_custom_attributes`
- [ ] 3.6 `corelib/mod.rs`：注册 `__param_custom_attributes`
- [ ] 3.7 `cargo test --release`（含 zbc_reader_tests version-pin）

## 阶段 4: stdlib
- [ ] 4.1 `ParameterInfo.z42`：`GetCustomAttributes()` / `GetAttribute(Type)` + 内部 `__qualified`/`__position`

## 阶段 5: z42c 同步（待 Decision 1）
- [ ] 5.1 `ZbcFormat.z42`：`ZbcVersion.Minor` → 15（+注释）
- [ ] 5.2 `ZbcWriter.z42`：SIGS BuildXxx 镜像 per-param 块
- [ ] 5.3 `zbc_tests.z42`：`test_zbc_empty_byte_identical` 247B golden 重截（regen 后 empty/source.zbc）
- [ ] 5.4 `z42 xtask.zpkg test compiler-z42` 绿

## 阶段 6: 验证
- [ ] 6.1 `src/tests/types/param_attributes.z42`（golden e2e）
- [ ] 6.2 `reflection.z42` 追加参数 attribute [Test] 断言
- [ ] 6.3 `docs/design/language/reflection.md` + `attributes.md` 同步（parameter 目标落地）
- [ ] 6.4 完整 GREEN gate（dotnet + cargo + vm + cross-zpkg + lib + zbc/zpkg invariant + compiler-z42）
- [ ] 6.5 spec scenarios 逐条覆盖确认
- [ ] 6.6 归档 + 释放四锁

## 备注
- **z42c 锁**：阶段 5 依赖 z42c 空闲；Decision 1 选 A 则等 port-z42c-tsig 归档后整体实施。
- regen 后 stdlib/test zbc 全失效，跑 `z42 xtask.zpkg regen`。
