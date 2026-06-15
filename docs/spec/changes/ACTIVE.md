# 活跃变更：子系统占用账本

> 规则见 [`.claude/rules/parallel-development.md`](../../../.claude/rules/parallel-development.md)。
> **一个子系统同一时刻只允许一个 in-flight change 持有。** 开 change（阶段 2）前查此表，被占则排队；归档（阶段 9）后释放。
> `docs` 不上锁（见协议）。

## 子系统持有表

| 子系统 | 当前持有 change | 起始 | 说明 |
|--------|----------------|------|------|
| `compiler` | —（空闲）| — | ~~add-reflection-get-interfaces~~ ✅ 已归档 2026-06-14（zbc 1.17/zpkg 0.19，TYPE section 类接口块 + IrClassDesc.Interfaces + ZbcReader round-trip）|
| `runtime` | **add-reflection-generic-predicates（进行中）** | 2026-06-14 | reflection: `__type_is_generic`/`_generic_def`/`_primitive` builtin。（~~redirect-golden-zbc-to-artifacts~~ ✅ 已归档 2026-06-16：`zbc_compat.rs` golden 发现改读 artifacts 镜像；~~tidy-examples-dir~~ ✅ 已归档 2026-06-15）|
| `stdlib` | **add-reflection-generic-predicates（进行中）** | 2026-06-14 | `Std.Type.IsGenericType`/`IsGenericTypeDefinition`/`IsPrimitive` |
| `z42c` | **port-z42c-self-compile（进行中）** | 2026-06-16 | dogfood gap-batch：z42c 自编译全部 7 自身包（G1-G8 已落地，**功能性自举达成**；下一级=逐包 byte-identical）。前序全归档：…→~~sync-z42c-zbc-117-interfaces~~✅（zbc 1.17 接口块 writer）/~~fix-z42c-irdump-gate-bugs~~✅/~~fix-z42c-load-fixup-loop~~✅（runtime 死循环根因 needs_fixup 不收敛，User 授权跨锁修）2026-06-16 |
| `toolchain` | **add-desktop-platform-backend（进行中，User 授权 2026-06-16）** | 2026-06-16 | desktop 作第 4 平台后端(C-ABI R1–R7 harness + DesktopBackend + 退役 host/examples 死副本)；主动新文件 + 删独立死副本。（~~redirect-golden-zbc-to-artifacts~~ ✅ 已归档 2026-06-16：xtask regen/test-vm golden run-.zbc → `artifacts/build/golden/` 镜像；~~infra-ci-platform-test-dashboard~~ ✅ 2026-06-16）|

## 全部 in-flight change（参考，子系统占用以上表为准）

| change | 子系统（待逐个确认） |
|--------|---------------------|
| scaffold-z42c-selfhost | z42c + compiler（已提交 127b7f11；gate 确认中，归档待绿）|
| port-z42c-core | z42c + toolchain |
| ~~port-z42c-codegen~~ | z42c —— ✅ 已归档 2026-06-09（CG-1A–2，210 cases）|
| ~~port-z42c-zbc-writer~~ | z42c —— ✅ 已归档 2026-06-10（功能完整 .zbc writer + empty 逐字节 + e2e 四向；DBUG/span 移交 add-z42c-source-spans）|
| ~~add-z42c-source-spans~~ | z42c —— ✅ 已归档 2026-06-10（span→DBUG + byte-compare 3/3；7942ab7d）|
| ~~port-z42c-zpkg-build~~ | z42c —— ✅ 已归档 2026-06-10（z42c build 端到端+直跑；e1ff3503）|
| ~~port-z42c-tsig~~ | z42c —— ✅ 已归档 2026-06-10（zpkg 全文件 byte-identical 2/2；05e615cf）|
| ~~port-z42c-import~~ | z42c —— ✅ 已归档 2026-06-10（hello-stdlib byte-identical + import e2e；a1fa39d8）|
| ~~port-z42c-instance-import~~ | z42c —— ✅ 已归档 2026-06-11（textapp byte-identical；gate 4/4）|
| ~~port-z42c-char~~ | z42c —— ✅ 已归档 2026-06-11（charcheck byte-identical；zbc 4/4）|
| ~~port-z42c-try~~ | z42c —— ✅ 已归档 2026-06-11（trycheck byte-identical；zbc 5/5）|
| ~~port-z42c-interface~~ | z42c —— ✅ 已归档 2026-06-11（ifacecheck byte-identical；zbc 6/6）|
| ~~port-z42c-closures~~ | z42c —— ✅ 已归档 2026-06-11（closcheck byte-identical；zpkg 5/5）|
| ~~port-z42c-package-symbols~~ | z42c —— ✅ 已归档 2026-06-11（multifile byte-identical；zpkg 6/6）|
| ~~port-z42c-statics-arrays~~ | z42c —— ✅ 已归档 2026-06-13（sacheck byte-identical；zbc 7/7；commit 3774faa4）|
| inline-jit-safepoint-check | runtime（暂停，不占锁） |
| investigate-concurrent-gc-stale-mark-race | runtime（暂停，不占锁） |
| ~~add-export-command~~ | toolchain —— ✅ 已归档 2026-06-14（`z42 export ios/android/wasm`；`[platform.*]` toml；`runtimes/<rid>/<ver>/` 平台 SDK；launcher_export*.z42 4 文件；0292c3a3）|
| ~~split-release-runtime-package~~ | toolchain —— ✅ 已归档 2026-06-14（`z42-runtime-*` 独立包；SDK 当 launcher；release-index.json runtime/launcher 子键；9 RID；4bbbd01b）|
| migrate-scripts-to-z42 | scripts/ + toolchain（不改 src/libraries/，不占 stdlib 锁）|
| add-z42-wasm-playground | runtime? / toolchain?（待回填） |
| ~~add-reflection-mvp~~ | runtime + stdlib —— ✅ 已归档 2026-06-09（feat 30776fae）|
| ~~make-typeof-return-type~~ | compiler + runtime —— ✅ 已归档 2026-06-09（C2，option A）|
| ~~add-attribute-reflection~~ | compiler + runtime + stdlib —— ✅ C3a（class-level）已归档 2026-06-09（feat 56d9cefb + 1377bfdb）|
| ~~add-attribute-reflection-methods~~ | compiler + runtime + stdlib —— ✅ C3b（method-level）已归档 2026-06-09（SIGS attr refs，zbc 1.11/zpkg 0.13）|
| ~~add-apphost~~ | toolchain —— ✅ 已归档 2026-06-09（feat a3720a16；per-app 原生 apphost，framework-dependent + 本地优先 + macOS 重签名）|
| ~~add-launcher-install~~ | toolchain —— ✅ 已归档 2026-06-13（`z42 install/self-update`；manifest-first + SHA256 + tgz/zip 流式解压；install-z42.sh --system/--dest/--dry-run/--verbose；删 install.sh；017c8116）|
| ~~fix-dist-runner-test-dirs~~ | toolchain —— ✅ 已归档 2026-06-09（dist runner 跳过 [Test]-based lib 测试 dir，修 secp256k1 误失败；镜像 GoldenTests.ContainsTestRunnerAttribute）|
| ~~fix-fqn-class-resolution~~ | compiler + stdlib —— ✅ 已归档 2026-06-09（ResolveMemberType namespace-aware FQN→类；移除 C3b FindByType workaround；GoldenTests 1545/1545）|
| ~~add-reflection-properties~~ | runtime + stdlib —— ✅ 已归档 2026-06-09（`Type.GetProperties()` + `PropertyInfo`，纯 runtime 派生自 get_/set_，无 zbc 格式 bump；GoldenTests 1549/1549）|
| ~~add-reflection-type-flags~~ | compiler + runtime + stdlib —— ✅ 已归档 2026-06-10（`Type.IsAbstract`/`IsSealed`；zbc 1.12 / zpkg 0.14，TYPE section flags 字节；cargo 795/0 + GoldenTests 1552/1552）|
| ~~add-cli-nested-subcommands~~ | stdlib —— ✅ 已归档 2026-06-10（`Std.Cli` 嵌套 `AddRouter`/`Resolve`/`CommandResolution`；14 新 [Test]；GREEN 269 文件/22 lib）。② xtask/launcher 迁移（合流 migrate-scripts-to-z42）解锁可开 |
| ~~add-reflection-static-fields~~ | compiler + runtime + stdlib —— ✅ 已归档 2026-06-10（`GetFields()` 含静态 + `FieldInfo.IsStatic`；zbc 1.13 / zpkg 0.15，TYPE section 静态字段块；cargo 797/0 + GoldenTests 1553/1553）|
| ~~add-cli-optional-positional~~ | stdlib —— ✅ 已归档 2026-06-10（`ArgParser.AddOptionalPositional`，z42.cli；11 新 [Test]）。migrate-xtask 前置 |
| ~~fix-reflection-test-compile~~ | stdlib + docs —— ✅ 已归档 2026-06-10（add-field 遗留 reflection.z42 编译失败：`none` 关键字 + 限定基名 `: Std.Attribute` upcast；改 `noneAttrs` + unqualified `: Attribute`；dotnet test 1554/1554。根因 compiler FQN-upcast bug 入 reflection.md Deferred `attr-factory-qualified-base-upcast`）|
| ~~migrate-xtask-launcher-to-std-cli~~ | toolchain —— ✅ 已归档 2026-06-10（xtask+launcher → Std.Cli 嵌套 router；`package`/`feature-matrix` 提顶层；删 `lib` 别名；每层 help；CI build package→package ×5。xtask GREEN 270/22 + test dist 347/0）|
| ~~add-field-attribute-reflection~~ | compiler + runtime + stdlib —— ✅ 已归档 2026-06-10（字段级用户 attribute 反射 `FieldInfo.GetCustomAttributes()`；zbc 1.14 / zpkg 0.16；cargo 799/0 + GoldenTests 1554/1554；参数 attr = follow-up）|
| ~~add-reflection-value-record-flags~~ | runtime + stdlib —— ✅ 已归档 2026-06-10（`Type.IsValueType` / `Type.IsRecord`，读 type-flags 已捕获的 struct/record 位；**无格式 bump**；cargo 800/0 + GoldenTests 1554/1554）|
| ~~fix-chained-property-dispatch~~ | compiler —— ✅ 已归档 2026-06-10（链式 getter 派发 `obj.GetType().BaseType.Name`；唯一根因 P1=Object stub GetType 返 Unknown→改取真实 Std.Type；纯 typecheck，无 zbc 格式/字节漂移；dotnet GoldenTests 1555/1555 + 链式 e2e 全过）|
| ~~add-parameter-attribute-reflection~~ | compiler + runtime + stdlib + z42c —— ✅ 已归档 2026-06-10（参数级用户 attribute 反射 `ParameterInfo.GetCustomAttributes()`；zbc 1.15 / zpkg 0.17，SIGS 每参数 attr-ref 块；z42c writer 同步；dotnet 1556/1556 + param_attributes.z42 e2e + cargo 757+21 + format 78。xtask gate 阻塞于 pre-existing 多文件 project-build 命名空间双重限定 bug → User 裁决单独 fix change 跟踪，本 change 走 dotnet 权威门）|
| ~~fix-multifile-project-namespace-qualify~~ | compiler —— ⚠️ **误判更正 2026-06-11**：xtask `undefined fn Z42Xtask.Std.Int32.Parse` 是**陈旧/混版产物幻影**，非真 bug。干净 worktree（origin/main）全量重建后 `xtask test vm`=346/0、零 undefined-function。gate 在干净树正常。仅存真 bug=niche 命名空间限定自由调用 `ns.func()`→`ns.ns.func`（xtask/常规不触发，低优先，待独立评估）。详见 memory `reference_multifile_project_namespace_double_qualify_bug`|
| ~~align-type-memberinfo-hierarchy~~ | stdlib + runtime —— ✅ 已归档 2026-06-11（`Std.Type : MemberInfo`；`typeof(C) is MemberInfo` 真，`Name` 由基类统一提供；短名基类无需编译器改动、无格式 bump；dotnet 1557/1557 + type_is_memberinfo.z42 e2e + cargo 759+21）|
| ~~fix-stale-build-stdlib-sh-refs~~ | runtime + compiler + toolchain —— ✅ 已归档 2026-06-11（报错/注释里 `./scripts/build-stdlib.sh` → `z42 xtask.zpkg build stdlib`；该脚本已折进 xtask；cargo+dotnet build 绿）|
| ~~fix-namespace-qualified-free-call~~ | compiler —— ✅ 已归档 2026-06-11（`ns.func()` 命名空间限定自由调用此前误绑 static-call-on-class `ns` → 双重限定 `ns.ns.func` 运行期 undefined；修 BindMemberCallOnUnknownTarget 识别同 ns 自由调用 → Free call；dotnet 1558/1558 + packed repro exit 0。即 fix-multifile-project-namespace-qualify 幻影下暴露的唯一真 bug）|
| ~~slim-instruction-enum~~ | runtime —— ✅ 已归档 2026-06-11（`Instruction` enum 96B→32B：装箱 15 个带 String 冷变体 `Variant(Box<XxxInsn>)`，热算术变体 inline；internally-tagged newtype 自动摊平保 JSON wire format；**无 zbc bump**，fixture 6/6 无字节 delta；cargo 806/0 + vm goldens 346/0）|
| ~~cache-cross-zpkg-call-target~~ | runtime —— ✅ 已归档 2026-06-11（review.md C7：per-site `OnceLock<Arc<Function>>` cross-zpkg Call 目标缓存，平行 method_tokens；首次解析后借用，消除每调用 `try_lookup_function` String hash；本模块快路径零额外开销；OnceLock write-once、per-site 无全局并发表；**无格式 bump**；cargo lib 764/0 + vm goldens 346/0 + cross-zpkg 2/0）|
| ~~add-reflection-inherited-static-fields~~ | runtime —— ✅ 已归档 2026-06-11（`Type.GetFields()` 含继承静态字段：`builtin_type_fields` 沿 base 链聚合祖先类 static_fields；对齐 C# 默认含继承公共静态；无格式 bump；dotnet 1559/1559 + inherited_static_fields.z42 e2e + cargo 764+21）|
| ~~add-reflection-parameter-names~~ | runtime —— ✅ 已归档 2026-06-11（`ParameterInfo.Name` 返真实源参数名：`resolve_func_sig` 读 Function DBUG local-vars（reg==参数索引），无符号回落 `arg{n}`；无格式 bump；dotnet 1560/1560 + parameter_names.z42 e2e + cargo 764+21）|
| ~~add-collection-contract-phase2~~ | stdlib —— ✅ 已归档 2026-06-11（review.md S2.4 Phase 2：`IBasicCollection<T>.AddOne(T)` + 5 集合委托自然 add；`BasicCollectionContract` add→count→clear 生命周期（distinct 元素普适 SortedSet 去重）；新增 Queue/Stack contract [Test]；无格式 bump；test stdlib z42.collections 5/5 + z42.test 5/5）|
| ~~add-reflection-array-element-type~~ | compiler + runtime + stdlib —— ✅ 已归档 2026-06-14（数组运行期不擦除 `GcRef<ArrayObj{element_type,elems}>`；`Type.IsArray`/`GetElementType()` + 非擦除 `arr.GetType()`；zbc 1.16/zpkg 0.18 ArrayNew/Lit element_type FQ 名；dotnet 1561/1561 + array_element_type.z42 e2e interp+jit + cargo lib 807 + 集成 native/zbc_compat 全绿 + xtask vm 354/cross-zpkg 2/stdlib 272）。**z42c writer 同步延后**（User 决策"先实现，延后 z42c"；当时 z42c 锁被占）→ `xtask test compiler-z42` byte-identical gate 暂红，follow-up 跟踪（见 memory `project_z42c_selfhosting`）|
| ~~add-reflection-get-interfaces~~ | compiler + runtime + stdlib —— ✅ 已归档 2026-06-14（`Type.GetInterfaces()` 含继承接口；zbc 1.17/zpkg 0.19 TYPE section 类接口块；base-walk 聚合 + 按名 dedup；dotnet 1562/1562 + get_interfaces.z42 e2e interp+jit + cargo lib 807 + 集成 zbc_compat/native 全绿 + xtask vm 356/cross-zpkg 2/stdlib 272）。z42c writer 接口块镜像延后（follow-up，同 array-element-type 处理）|
| plan-0.3.x-three-streams | docs（不上锁） |
