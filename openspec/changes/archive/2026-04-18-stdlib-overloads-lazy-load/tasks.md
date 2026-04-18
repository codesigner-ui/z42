# Tasks: stdlib object 重载 + 隐式依赖 + 延迟加载

> 状态：🟢 已完成 | 创建：2026-04-18

**变更说明：**
1. stdlib 常用方法改为 `object` 参数（通过类型系统的 universal-to-object 规则）
2. 编译时 stdlib 默认全可见，不需 `using` 声明
3. VM 运行时延迟加载：只主动加载 core，其他 zpkg 首次调用时 lazy 加载（仅 interp 模式；JIT 仍 eager）

## B1: 类型系统 ✅
- [x] `Z42Type.IsAssignableTo`: 任何非 Void 类型可赋给 object

## B2: stdlib 改为 object 参数 ✅
- [x] Console.WriteLine/Write → (object)
- [x] StringBuilder.Append/AppendLine → (object)
- [x] 开启导入类的严格参数检查（不再宽松模式）

## C1: 编译时 stdlib 默认全可见 ✅
- [x] PackageCompiler.CompileFile 使用 TsigCache.LoadAll 而非 LoadForUsings
- [x] SingleFileCompiler 也加载 stdlib TSIG，不需 using
- [x] ImportedSymbolLoader.Load 以所有可用命名空间为过滤器

## C2: VM 延迟加载 ✅
- [x] 新增 metadata/lazy_loader.rs：thread_local + namespace → zpkg 映射 + 按需加载
- [x] Call 指令：module.functions miss → 咨询 lazy_loader
- [x] VCall 指令：module.functions miss → 咨询 lazy_loader
- [x] main.rs 注册 lazy_loader；interp 模式纯 lazy，JIT/AOT 保持 eager（JIT 需要预编译）

## 验证 ✅
- [x] dotnet build && cargo build 全绿
- [x] dotnet test: 442 passed
- [x] ./scripts/test-vm.sh: 114 passed
- [x] 手动验证：不带 using 的 stdlib 调用能编译 + 运行
