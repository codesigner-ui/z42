# Tasks: port-z42c-try

> 状态：🟢 已完成 | 创建+批准+实施+归档：2026-06-11 | 子系统锁：z42c（已释放）

- [x] TR-1 Bound 三式 + TypeChecker 绑定 + 单测
- [x] TR-2 codegen lowering（标签/表/合成 catch-all/throw）+ 单测
- [x] TR-3 IrExcEntry + IrFunction 可变表 + ZbcWriter 编码/intern
- [x] TR-4 trycheck 第 5 zbc 源（执行+byte-compare 5/5）+ gate + 文档 + commit

## 实施记录（2026-06-11）
- 整链落地：BoundTry/BoundCatchClause/BoundThrow → _emitTry（标签 try_start/try_end/after_try/finally 1:1 C#，合成 catch-all "*"+rethrow）→ IrExcEntry（可变公字段挂 IrFunction，D1 免 16 参 ctor 全站改）→ FUNC exc 条目编码（offsets 后 instr 前）+ intern 位（块串后 LineTable 前）。
- **字节校准两条**：①C# 对 try/catch/finally 体走 EmitBoundBlock **不 TrackLine 块自身**（我多 2 条 DBUG 32B）；②catch 变量 = **Alloc+Copy 专用寄存器**（var-decl 同款，非别名——REGT +1 reg、FUNC +6B 实证）。
- **corpus 约束发现**：C# 单文件模式 catch 类型强校验（E0420 须 derive from Exception）且解析不了 stdlib Exception → trycheck 用**本地 Exception 基类 + 派生 MyErr**（自包含双侧公平）。z42c typecheck 暂无 E0420 校验（更松，挂账）。
- trycheck 第 5 zbc 源：throw/typed catch/finally + oracle → 执行 ✓ + byte-compare **5/5**。
- 延后：catch when / E0420 校验 / catch 顺序窄化。
