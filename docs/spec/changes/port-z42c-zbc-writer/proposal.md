# Proposal: port-z42c-zbc-writer — IrModule → byte-identical .zbc

## Why

z42c 的 Bound→IR 内存模型 + lowering 已完成（port-z42c-codegen，CG-1A–2，210 cases）。
但 IR 只是内存对象 + 文本 dump——**不能执行**。真正能产出可加载/可执行产物的下一段是
**`.zbc` 二进制序列化**：`IrModule → bytes`。

这是 0.3.x B 主线「byte-identical 自举」的硬核：z42c 写出的 `.zbc` 必须与 C# 编译器
（`z42.IR/BinaryFormat/ZbcWriter.cs`）**逐字节相同**，z42vm 才能无差别加载执行 z42c 产物。
完成后，核心编译路径 `syntax✅ → semantics(typecheck✅+codegen✅) → ZbcWriter → .zbc` 贯通，
z42c.driver 可产出真正能跑的 zpkg。

## What Changes

- **z42c.ir 加 `BinaryFormat/`**：从零镜像 C# `ZbcWriter.cs` + `ZbcWriter.Instructions.cs` +
  `Opcodes.cs` + `StringPool.cs` + `TokenAllocator.cs`：
  - `ByteWriter`：可增长字节缓冲 + LE 写助手（u8/u16/u32/i64/f64/bytes/str）。
  - `Opcodes`：opcode 常量表（int 常量，镜像 C# enum）+ TypeTags。
  - `ZbcStringPool`：插入序保留的字符串池（≠ IrModule 的 const.str 池；STRS 含名字/类型/字段等）。
  - `TokenAllocator`：函数/类名 → intra-module index（插入序）或 import token。
  - `ZbcWriter`：header + section directory + 各 section（NSPC/STRS/SIGS/FUNC/…）+ 指令编码。
- **验证**：golden hex（从 C# z42c 同源产物截取字节，断言 z42c 输出逐字节相同）。

## Scope（允许改动的文件）

> 多增量 change：下表是 ZW-1A（最小：trivial 函数 const+算术+ret → byte-identical）+ 模型骨架的核心文件。后续增量向 Opcodes/ZbcWriter 追加 opcode 与 section。新增文件触发时回阶段 3 更新本表。

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.ir/src/BinaryFormat/ByteWriter.z42` | NEW | 可增长字节缓冲 + LE 写助手（WriteU8/U16/U32/I64/F64/Bytes/Str + ToArray）|
| `src/z42c/z42c.ir/src/BinaryFormat/Opcodes.z42` | NEW | opcode int 常量表（ConstI/Add/Ret/…）+ TypeTags（i32→0x04 等）+ SectionTags |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcStringPool.z42` | NEW | 插入序字符串池（Intern→idx，list+index map）|
| `src/z42c/z42c.ir/src/BinaryFormat/TokenAllocator.z42` | NEW | 函数/类名 → intra-module index / import token（FromModule + ResolveMethod/ResolveType）|
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcWriter.z42` | NEW | 主写入：Write(module)→bytes；header+directory+section 组装 |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcInstr.z42` | NEW | 指令/终结符编码（集中 if-is：每 IrInstr → opcode+type_tag+dst+operands）|
| `src/z42c/z42c.ir/tests/zbc/zbc_tests.z42` | NEW | golden-hex 单测（trivial 函数 → 字节断言）|
| `src/z42c/z42c.ir/tests/zbc/z42c.ir.test.zbc.z42.toml` | NEW | 测试单元 manifest |
| `src/z42c/z42c.ir/README.md` | MODIFY | 加 BinaryFormat/ 段 |
| `docs/design/compiler/self-hosting.md` | MODIFY | 进度：zbc-writer 子段（归档时）|

**只读引用**（理解上下文，不改）：

- `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` / `ZbcWriter.Instructions.cs` / `Opcodes.cs` / `StringPool.cs` — 字节格式蓝本
- `src/compiler/z42.IR/Tokens.cs` / `TokenAllocator.cs` — token 蓝本
- `src/z42c/z42c.ir/src/IrModule.z42` / `IrInstr.z42` / `IrTerminator.z42` — 序列化输入
- `docs/design/runtime/zbc.md` — zbc 格式权威规范

## Out of Scope

- **ZbcReader（.zbc → IrModule）** —— 本 change 只做 Writer（z42vm 已有 Rust reader 加载 z42c 产物）。
- **可选 section**：DBUG（调试信息）/ TIDX（test 元数据）/ BLID（BLAKE3 build-id）/ FRCS —— 延后增量。
- **native interop opcodes**（CallNative/Pin/Unpin）/ 闭包（MkClos/LoadFn）/ 异常表 —— codegen 未产出，延后。
- **stripped/.cache 模式 + .zsym sidecar** —— 延后。
- z42c.driver build 命令产出 .zpkg 串联 —— 另起。

## Open Questions

- [ ] **f64 字节重解释**：写 double 的 8 字节 IEEE-754 需把 double 的原始 bits 取出（C# `BinaryWriter.Write(double)`）。z42 是否有 `BitConverter.DoubleToInt64Bits` 等？无则需 stdlib 补（ZW-1A trivial 函数无 float，可延后到 float 增量）。
- [ ] **验证策略**：golden hex（截 C# 输出）vs 端到端（z42c 写 .zbc → z42vm 加载执行）vs xtask 全量对账。ZW-1A 拟用 golden hex（直接验字节）；端到端/全量对账后续。待 design 决策。
- [ ] **version major/minor**：当前 zbc 1.9（2026-05-30）。z42c 须写相同版本号；版本随 C# bump 同步。
