# Design: port-z42c-import — 跨包 import 消费侧（MVP：静态调用 + 自由函数）

> 状态：DRAFT（待 User 审批）｜来源：Explore agent 全链 survey（ImportedSymbolLoader 6 文件/PipelineCore/FunctionEmitterCalls/BuildDependencyMap/ZpkgReader，行级引用在 survey 报告）+ tsig 期字节解码经验。

## Architecture

```
driver build：
  Z42_LIBS 目录 → DepScan（prelude-first Ordinal 排序，每 zpkg：ZpkgReader.ReadMeta+ReadSigs+ReadTsig+ReadNspc）
      ├─ DependencyIndex.Build（SIGS 签名 → static/instance 键表）          [CI-2]
      ├─ nsMap：ns → zpkg basename（first-wins）                            [CI-4 DEPS 用]
      └─ ExportedModuleZ[]（TSIG）→ ImportedSymbolLoader.Load(usings∪prelude 过滤)  [CI-3]
            Phase1 骨架 → Phase2 填充 → ImportedSymbols{Classes/Functions/ClassNamespaces}
  每源文件：parse → SymbolCollector(+imported 合并) → TypeChecker → IrGen(depIndex)
      ExprEmitter 静态调用：DepIndex.TryGetStatic("Console","WriteLine$1"→"WriteLine")
          命中 → CallInstr(QualifiedName="Std.Console.WriteLine") + TrackDepNamespace("Std")
          未中 → 既有本地 Qualify 路径
  ZpkgBuilder：DEPS = BuildDependencyMap(usings ∪ usedDepNs, nsMap)
  ZbcWriter：IMPT 既有逻辑自动收集外部 Call 名（已实现 ✓）；TokenAllocator ImportBase 已实现 ✓
```

## Decisions

### D1：MVP 面 = 静态类方法 + 自由函数（实例方法延后）
C# 实例命中链带 receiver-aware 防护（ReceiverChainHasMethod 防 stdlib 劫持用户方法）+ VCall fallback + FillDefaults——整套是独立大块。MVP corpus（hello-stdlib）只需静态：`Console.WriteLine`。实例路径保持现状（VCall 短名，运行时分派——z42c 已有且 e2e 已验证可执行）。**byte-identity 对 corpus 成立即收口**；扩 corpus 时再补实例命中链。

### D2：Reader 不解码 FUNC 体
DepIndex 只用 IrFunction 的 Name/IsStatic/ParamCount/RetType（survey §5 实证）——全部来自 zpkg SIGS 平铺。z42c ZpkgReader 从 SIGS 合成 IrFunction 签名 stub（Blocks 空），按 MODS 的 fnCount/firstSigIdx 切回每模块 + ns。FUNC 体解码（ZbcReader 全量）留给未来的 indexed/增量需求。

### D3：imported 类注册形态贴 z42c 既有约定
C# 分 Methods/StaticMethods 两 dict；z42c Z42ClassType 只有 Methods（收集器静态/实例同表，typecheck _bindMemberCall 按"裸类名 receiver→static"区分）。imported 类沿用：方法全进 Methods（MethodSymbol.IsStatic 标真值），**不**为 import 改 Z42ClassType 形态（设计完整性：消费端不为新需求加平行结构；将来 typecheck 需要区分时一次性升级两路）。

### D4：DEPS = usings ∪ usedDepNs → nsMap（镜像 BuildDependencyMap）
nsMap：扫描 libs 目录每 zpkg 的 NSPC 全名单 → ns→basename first-wins（prelude-first 排序保确定）。File=basename。intra-package ns 不记（MVP 单模块工程，本 ns 不在 nsMap 即天然跳过）。**条目序/ns 序 = C# 遍历序**（units 序 + 首现序去重）——逐字节校准。

### D5：激活包过滤 = prelude ∪ usings 提供包
镜像 strict-using-resolution：activated = PreludePackages（z42.core 等固定名单，校准 C# PreludePackages.Names）∪ {提供 cu.Usings 中 ns 的包}。TSIG 模块不在激活集 → 不进 ImportedSymbols。

### D6：TypeResolver 子集优先级
数组后缀 `T[]` → prim（int/string/bool/...）→ imported class 名 → fallback Z42PrimType(name)（z42c 既有 Unknown 吸收兼容）。泛型实例化串（`List<int>`）MVP fallback 处理（corpus 不含）。

## Implementation Notes
- ExprEmitter 静态命中点放在现 `c.Kind == "static"` 分支前段：先 DepIndex（арity 键→bare 键），未中走 Qualify 本地。TrackDepNamespace 进 EmitContext（StrMap 当 set 用 + 首现序数组——DEPS 序要确定）。
- ZbcFileZ.UsedDepNamespaces：string[]+count，driver 透传到 ZpkgBuilder。
- prelude 包名单：以 C# `PreludePackages.Names` 实测为准（grep 校准）。
- 重载 bare 键：C# Phase2/DepIndex 注册序 first-wins——hello-stdlib 的 WriteLine(string) 命中以同源字节校准（Q1）。

## Testing Strategy
- 单测：reader 用 **z42c 自产 zpkg**（ZpkgWriterZ 输出）往返断言 META/SIGS/TSIG/NSPC；DepIndex 静态/实例键 + 歧义剔除 + prelude-first；loader 骨架→填充（Console.WriteLine 签名可查）；DEPS map（usings+usedNs→条目）。
- e2e（gate 第 3 工程）：hello-stdlib `using Std; void Main(){ Console.WriteLine("hi"); }` → z42c build → z42vm 直跑 stdout 含 hi + vs C# CLI 全文件 cmp。
- 回归：既有 15 units + zbc 3/3 + zpkg 2/2 全保持。

## Deferred
实例方法命中链（receiver-aware）/Console 变参糖/FillDefaults/命名参数/Phase3 impl/接口·委托·枚举 import/E0601/indexed 读取/ZbcReader FUNC 体解码。
