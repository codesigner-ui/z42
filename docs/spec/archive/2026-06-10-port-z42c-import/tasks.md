# Tasks: port-z42c-import

> 状态：🟢 已完成 | 创建+批准+实施+归档：2026-06-10 | 子系统锁：z42c（已释放）

## 进度概览
- [x] CI-1 ZpkgReader 子集（META/NSPC/SIGS/TSIG；不解码 FUNC 体）+ 自产 zpkg 往返单测
- [x] CI-2 DependencyIndex（static/instance 键 + 歧义剔除）+ DepScan（prelude-first 扫描 + nsMap）+ 单测
- [x] CI-3 ImportedSymbolLoader Phase1/2 子集（类骨架→成员填充 + 自由函数 + TypeResolver 子集 + usings∪prelude 过滤）+ 单测
- [x] CI-4 接线：SymbolTable 合并 / ExprEmitter 静态 DepIndex 命中 + TrackDepNamespace / DEPS 真实条目 / driver 组装
- [x] CI-5 e2e hello-stdlib（直跑 + 全文件 byte-compare 第 3 工程）+ gate 全绿 + 文档（README×4 + self-hosting.md）+ commit

## 实施记录（2026-06-10）

**🎉 z42c 第一次编译并产出 stdlib-using 程序**：`using Std; Console.WriteLine("hi")` → z42c build → z42vm 直跑输出 hi，且 **全文件 byte-identical vs C# CLI（2549B，DEPS=[z42.core(Std), z42.io(Std.IO)]）**——gate zpkg byte-compare 升 3/3 + import e2e 直跑步常驻。

**实施中字节校准的真相**：
① **no-ns 单元在 build 路径 ns="main"**（EXPT="main.Main"/MODS ns="main"），而 entry 检测输入是**未限定原始函数名**（entry="Main"）——两个约定来自不同数据源（zbc.Namespace vs unit.Exports）
② **C# nsMap（DEPS file 解析）是 BuildLibsDirs 布局依赖的**：walk-up 仓库时按成员目录字母序扫（z42.cli<z42.core 且都声明 ns Std）→ 仓库内 Std→z42.cli.zpkg、/tmp 下单目录 prelude-first→z42.core.zpkg。e2e hello 工程放 /tmp 隔离（两侧环境等价）；z42c 自身只扫 Z42_LIBS 单目录（MVP 约定）
③ **zbc 1.15/zpkg 0.17 在实施中落地**（add-parameter-attribute-reflection：SIGS 每参 per-param attr 块）且并行流又跳过 checklist 第 5 步——本 change 顺带同步（reader skip + writer 块 + 版本 + empty/f5 golden 重截）；WriteConstraintBundle 真布局（flags→[base]→[tpRef]→ifaces→[bit6 funcSig]）与我初版猜测不同，SIGS 错位 262144 越界抓出
④ z42 新坑：`as string`（prim 下行）不可用 → StrBox 包装

单测：depindex 单元 ×4（键形态/FQ 短名兜底/арity first-wins/歧义剔除/协议方法跳过）。
延后（按 design）：实例方法命中链/Console 变参糖/接口·委托·枚举 import/Phase3 impl/indexed 读取。
