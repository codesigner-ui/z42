# Tasks: port-z42c-package-symbols

> 状态：🟢 已完成（自举首包冒烟移交 G3——见实施记录）| 创建+批准+实施+归档：2026-06-11 | 子系统锁：z42c（已释放）

- [x] PS-1 CollectAll 合并收集 + driver 两趟 + IrDump 包级入口
- [x] PS-2 arr.Length（typecheck + ArrayLenInstr + 编码 + REGT）+ 单测
- [x] PS-3 multifile 第 6 工程（byte-compare 6/6）+ z42c.core 自编译冒烟 + gate + 文档 + commit

## 实施记录（2026-06-11）

**multifile（namespaced 双文件互引）全文件 byte-identical（4522B）+ 双侧执行正确**——gate zpkg 对账升 6/6。PS-1 两趟 build（CollectAll 三段 pass 合并 + BuildPackage 包级入口 + per-file IrModule 不变）；PS-2 arr.Length（ArrayLen 0x84 整链 + target 静态类型判别防类字段同名误击）落地（单测；e2e 等 G4 数组创建）。

**校准/发现**：
① **C# TSIG functions 兄弟泄漏**（sem.Funcs 合并无过滤 → 每模块 functions=全包自由函数，classes 有 intra 过滤不重复——13B/条实证）→ ExtractP 镜像
② **C# no-ns 多文件互引自身有 pre-existing VCall 缺陷**（`main.Pair.Sum not found`——stdlib 全 ns 故无人踩；挂账上游）→ corpus 用 namespaced
③ **z42c 数组创建 `new T[n]` 是独立缺口 G4**（双侧 corpus 都翻车）→ arr.Length e2e 顺延
④ **_compileCu 漏透传 ImportedClassNs** → ObjNew 短名 → 运行期 vtable miss；**且当次 gate 因 C# 撞并行 stdlib 半重写同样致盲 → byte-compare "通过"而产物坏——run 步抓住**（对账+执行双轨的防御价值实证）

**spec 偏差（移交下一 change）**：自举首包冒烟（z42c.core 0 错产包）被 **G3 静态字段/常量访问**挡住（`DiagnosticSeverity.Error`——static class+int 常量 = z42 enum 惯用法；探针期与跨文件混淆，全包收集修好后水落石出）。G3+G4（数组创建）= 下一 change port-z42c-statics-arrays。
