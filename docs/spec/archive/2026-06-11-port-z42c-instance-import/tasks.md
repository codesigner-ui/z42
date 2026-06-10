# Tasks: port-z42c-instance-import

> 状态：🟢 已完成 | 创建+批准+实施+归档：2026-06-11 | 子系统锁：z42c（已释放）

## 进度概览
- [x] IC-1 receiver-aware 守卫 + VCall fallback 依赖追踪（ctx.ImportedClassNamespaces）
- [x] IC-2 DepIndex instance 捷径（CallInstr FQ + recv 前置）
- [x] IC-3 typecheck prim receiver 吸收 + 单测
- [x] IC-4 e2e textapp 第 4 工程（直跑+byte-compare）+ gate 全绿 + 文档 + commit

## 实施记录（2026-06-11）

**🎉 textapp 全文件 byte-identical（2974B）+ 直跑输出 "ab"**——gate zpkg 对账升 4/4。StringBuilder（imported 类 → VCall 短名 + DEPS 含 z42.text）+ `s.Substring(0,2)`（prim receiver → DepIndex instance 捷径 → `Std.String.Substring$2` FQ Call，recv 前置实参）。

**字节校准真相**：
① **prim receiver 的 typecheck 不是纯松绑定**：C# 经 CapitalizeFirst（"string"→"String"）映射到 stdlib 包装类（z42.core TSIG imported）取真实 ret 类型——dst tag 0x0D(Str) 非 Unknown（差 2 字节抓出）；查无才落 Unknown 吸收
② **TSIG 方法名带 $arity 重载后缀**（Substring$1/$2）——C# Phase2 同时注册 bare 键 first-wins，我漏了 → 包装类查 "Substring" miss → 补 bare 键注册
③ 实例分支寄存器序镜像 C#：**实参先于 receiver**（现 corpus receiver 全 local 不可见，防未来漂移）
④ imported 类 ObjNew/is/as 需 **QualifyClass**（查 ImportedClassNamespaces → "Std.Text.StringBuilder"，字节实证）——BP-0 的 Qualify 只盖模块 ns

延后（按 design）：Array builtin 路径 / FillDefaults / arity 重载选择。
