# Design: port-z42c-zpkg-build — `z42c build` → byte-identical packed `.zpkg`

> 状态：DRAFT（待 User 审批）｜来源：C# `ZpkgWriter*.cs`(261+367+232)/`ZpkgBuilder.cs`(179)/`PackageTypes.cs`/`PackageCompiler.BuildTarget.cs`(关键路径) 全量 map + `packed-minimal` fixture 字节解码。

## Architecture

```
z42c build <toml>
  → ManifestLoader（已有，port-z42c-project 机械段）
  → SourceDiscovery：[sources].include glob → 排序 .z42 列表        [BP-1 project]
  → 逐文件：Parser→TypeChecker→IrGen（BP-0 namespace 限定）→ IrModule
      ZbcFileZ{ SourceFile=项目相对路径, SourceHash=SHA256 大写 hex, Namespace, Exports=函数名, Module }
  → ZpkgBuilder.BuildPacked：namespaces 去重 + exports FQ 限定 + entry 自动检测   [BP-2]
  → ZpkgWriter.WritePacked：META/STRS/NSPC/EXPT/DEPS/SIGS/MODS 七段             [BP-3]
  → File.WriteAllBytes(<name>.zpkg)                                              [BP-4 driver]
```

zpkg packed 字节布局（fixture 实证，7 段）：
```
header: "ZPK\0" + major u16(0) + minor u16(同步 C#) + flags u16(0x01 Packed|0x02 Exe) + secCount + reserved
META: name(u16+utf8) + version(u16+utf8) + entry(u16+utf8；lib 为空串)
STRS: 统一池（zpkg 元串 → 每模块[ns/源路径/hash + InternPoolStrings 全量]；段构建复用 zbc 版）
NSPC/EXPT/DEPS: count + idx 表（EXPT 带 kind u8：func=0/type=1/const=2；DEPS MVP=0 条）
SIGS: 全模块函数签名平铺（布局 = zbc SIGS 每函数条目，含 tp/attr 块）
MODS: count + 每模块{ns_idx,src_idx,hash_idx,fnCount u16,firstSigIdx u32,
       func_len+FUNC体, type_len+TYPE体, dbug_len+DBUG体, regt_len+REGT体, tidx_len(0)}
       —— 体复用 z42c ZbcWriter 段构建器（全局池 + per-module remap + per-module TokenAllocator）
```

## Decisions

### D1：z42c ZbcWriter 段构建器共享化（前置重构）
现 `_buildFunc/_buildType/_buildSigs/_buildDbug/_buildRegt` 是 private(IrModule, pool)。zpkg 需要"函数列表/类列表 + 外部共享池 + 外部 remap + 外部 allocator"形态（镜像 C# `ZbcWriter.BuildFuncSection(functions, pool, remap, allocator)` 静态共享）。**重构为 public 静态、参数化**，zbc 路径与 zpkg 路径同源——杜绝两份编码漂移（C# 注释多次强调 "writers must stay in lock-step"，我们直接单源）。独立 refactor commit。

### D2：BP-0 namespace 限定放 IrGen（产出端）
fixture 实证：namespaced 源的类名/函数 key/ctor/exports 全 FQ（`Demo.Minimal.Greeter.Hello`）。限定在 **IrGen 入口一次性**做（类 desc 名、函数 key、ObjNew class/ctor 引用、VCall owner? —— VCall.Method 是短名 ✓ fixture 'Hello' 短名在池），镜像 C# QualifyName 于 IrGen/Emitter 层。无 namespace 源零变化（既有全部 golden/e2e 保持）。

### D3：entry 自动检测 = C# 四级优先
`.Main` FQ > 裸 `Main` > `.main` FQ > 裸 `main`；同级多候选 → 报错退出（E 同 C# 文案）。manifest `entry` 显式指定则跳过。kind=exe 无候选 → 报错；lib → entry 空串。

### D4：SourceHash / SourceFile
- hash = **源文本 SHA256 大写 hex**（fixture 64 字符大写实证）；z42 侧 `Std.Crypto.Sha256.Hash(bytes)` + 自写 hex-upper（String→bytes 用 UTF8 API，须核 stdlib：`Encoding.Utf8.GetBytes`？BP-2 首件确认，缺则按 dogfood 流程补/借 builtin）。
- SourceFile = **相对 projectDir 路径**（fixture "source.z42" 实证；跨机器可复现关键）。

### D5：验证双轨（同 zbc-writer 模式）
- **golden**：`packed-minimal` 同源（namespaced Greeter+Main）内嵌 hex 断言 byte-identical（771B 级；随 zpkg bump 由 version-bumping 第 5 步 regen——该步骤说明扩到 zpkg golden）。
- **e2e**：xtask 新步——临时工程（toml+源）→ `z42c build` → ① z42vm **直接跑 zpkg**（entry 烤入，验 META/MODS/SIGS 全链可加载执行）；② 同工程经 C# `z42c build` → 逐字节 diff。

## Implementation Notes

- **MODS 的 per-module remap**：zpkg 统一池下，每模块 `strRemap[i] = pool.Intern(module.StringPool[i])`（z42c 1-based 起 1）；先 intern zpkg 元串 → 再逐模块（ns/源路径/hash → InternPoolStrings 等价段）——**顺序 1:1 C# WritePacked**。
- **firstSigIdx**：模块函数在全局 SIGS 平铺中的起始下标（累计）。
- glob：MVP 支持 `**/*.z42` 与 `*.z42`（z42c 自身/测试工程够用）；结果 **Ordinal 排序**（common-pitfalls §1）。
- driver build 输出路径：MVP 写 `<projectDir>/dist/<name>.zpkg`（C# CentralizedBuildLayout 的 output_dir 模板解析延后；e2e 用默认即可）。
- `flags`：Packed 恒置；kind=exe 加 Exe 位。

## Testing Strategy
- 单元：SourceDiscovery 排序/glob；entry 四级+歧义；ZpkgBuilder exports 限定幂等；META/EXPT/DEPS 段 hex。
- Golden：packed-minimal 同源全文件 hex。
- e2e：build→直跑 zpkg + build byte-compare（xtask）。

## Deferred
indexed/sidecar/strip、TSIG/IMPL、增量、workspace 多成员、DEPS 真实解析、输出路径模板、`[[exe]]` 多入口。
