# Tasks: port-z42c-tsig

> 状态：🟢 已完成 | 创建+批准+实施+归档：2026-06-10 | 子系统锁：z42c（已释放）

## 进度概览
- [x] TS-1 ExportedTypes.z42 模型（ir）
- [x] TS-2 ExportedTypeExtractor.z42（semantics，CU 声明序）+ 单测
- [x] TS-3 ZpkgWriterZ TSIG/IMPL 两段 + intern 时机（deps 后、逐模块前）+ 单测
- [x] TS-4 driver build 接线（每文件 Extract → ZpkgFileZ.ExportedModules）
- [x] TS-5 xtask e2e 全文件 byte-compare（buildapp + demo.minimal ×2 工程）+ gate 全绿 + 文档（README×2 + self-hosting.md）+ commit

## 实施记录（2026-06-10）

**🎉 zpkg 包级对账达成：2 工程全文件 byte-identical（gate 常驻）**——check.app（exe：调用/对象/字段/自由函数，3576B）+ demo.minimal（namespaced 单类，2944B），z42c vs C# CLI（--strip-symbols=false）逐字节含 TSIG/IMPL 九段。

**D2 修订（字节实测推翻"0 计数"假设）**：C# 真 CLI 的 TSIG 含**编译器级固定内建面**——每用户类前置 Object 四方法（ToString/Equals/GetHashCode/GetType[flags=0]，virtual×3）+ 11 内建接口 + GCHandleType 枚举 + Action×5/Func×5/Predicate 委托。z42c 以静态表镜像（ExportedTypeExtractor._builtin*）。proposal 的 Out-of-Scope 前提（"z42c 前端没有就不用发"）误判——这些来自 C# SymbolCollector 的 prelude 注入，与用户源无关。

**校准出的五条字节真相（猜不到，全靠 dump）**：
① 方法/函数参数名 = "p{i}"、委托 = "arg{i}"（非源名！）；无修饰成员可见性 = "internal"
② TSIG 类基名 = 短名 "Object"（区别于 TYPE 段 "Std.Object"）；INumber 五方法 flags = static|abstract(5)
③ SourceFile = 完整路径原样、SourceHash = "sha256:"+小写（fixture 的相对路径/大写无前缀是测试合成形态——fixture 不能当真 CLI 的 oracle，第二次被骗）
④ **TypeTags.FromString 逐字镜像**："string"/"short"/"byte" 等都落 default→Object(0x20)，仅 str/int/long/double/float 命中——曾因从未测过 string-ret 函数而带错误多拼写（zbc 路径潜在 bug 一并修）
⑤ **REGT = 指令遍历收集**（dst 先、首个非 Unknown 赢、未引用寄存器=Unknown）非预填参数类型——Hello 未引用的 this 是 0x00（callcheck 当年碰巧对，因 this 都被 field_get 引用）
**+ prelude 漂移实例**：实施中并行反射流让 Object stub 构建时能解析 Type 类 → GetType ret 从 "unknown" 变 "Type"——C# 同日两次编译产物不同；byte-compare gate 正是为此类漂移而设（失败即警报）。

z42 受限写法：静态表构造无集合字面量 → _t0/_t1/_t2/_iface/_del 工厂函数族。
