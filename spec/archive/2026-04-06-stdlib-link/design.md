# Design: stdlib-link

## Architecture

```
BuildCommand.BuildTarget
  └─ 1. 扫描 libs/*.zpkg → nsMap (已有)
  └─ 2. 加载 stdlib zpkg → StdlibCallIndex (新增)
         ├─ 静态方法：key = "ClassName.MethodName" → QualifiedName
         └─ 实例方法：key = "ClassName.MethodName" → QualifiedName (IsInstance=true)
  └─ 3. CompileFile(sourceFile, stdlibIndex)
         ├─ TypeChecker(diags).Check(cu)           (不变，Unknown 回退)
         └─ IrGen(stdlibIndex).Generate(cu)
              └─ IrGenExprs.EmitCall
                   ├─ 用户定义类静态方法           (不变)
                   ├─ StdlibCallIndex 静态 → CallInstr(qualifiedName)
                   ├─ StdlibCallIndex 实例 → CallInstr(qualifiedName, [this, ...args])
                   └─ fallback → VCallInstr / CallInstr (不变)
  └─ 4. IrGen.UsedStdlibNamespaces → 注入 dependencies (不随 using 而是随实际调用)
```

## 新类型：StdlibCallIndex（z42.Project/StdlibCallIndex.cs）

```csharp
public sealed record StdlibCallEntry(
    string QualifiedName,   // "z42.io.Console.WriteLine"
    string Namespace,       // "z42.io"
    bool   IsInstance       // true = instance method (this = arg 0)
);

public sealed class StdlibCallIndex {
    // Key = "Console.WriteLine" or "String.Substring$1" (arity suffix for overloads)
    public IReadOnlyDictionary<string, StdlibCallEntry> Entries { get; }

    public static StdlibCallIndex Build(IEnumerable<ZpkgFile> stdlibPkgs);
    public bool TryGetStatic(string cls, string method, [out] StdlibCallEntry entry);
    public bool TryGetInstance(string cls, string method, int arity, [out] StdlibCallEntry entry);
    public bool TryGetInstanceByMethod(string method, int arity, [out] StdlibCallEntry entry);
}
```

## 提取逻辑（从 zpkg IrModule）

zpkg（packed 模式）中每个 IrFunction 的 Name 形如：
- `"z42.io.Console.WriteLine"` → 静态方法（类无实例字段）
- `"z42.core.String.Substring$1"` → arity-1 静态方法（带 this = arg0）
- 对应 class 是否为 static：检查 IrClassDesc 中 Fields 是否为空

判断是否为 native stub：IrFunction body = 单 block、单 BuiltinInstr（该条件可放宽，只要能找到 QualifiedName 即可）。

Short key 提取规则：QualifiedName 末两段 = `ClassName.MethodName`（去掉 namespace 前缀）。

## 实例方法分发策略

| 场景 | 策略 |
|------|------|
| `str.Substring(1)` — String 独有方法 | StdlibCallIndex 查 `String.Substring$1` → `CallInstr("z42.core.String.Substring$1", [str, 1])` |
| `str.Contains("x")` — String + List 都有 | StdlibCallIndex 只有 `String.Contains`（按类名区分）；IrGen 目前无 receiver 类型 → 回退 VCallInstr |
| `list.Add(x)` — List 独有方法 | 同上，按方法名查 Index 无歧义项 → CallInstr |

**歧义规则**：若同名方法在多个类中存在（且不同命名空间），则回退 VCallInstr，由 VM 在运行时解析。

## IrGen 修改要点

1. 构造函数新增 `StdlibCallIndex? stdlibIndex = null`（可选，向后兼容）
2. `IrGen` 新增 `_usedStdlibNamespaces: HashSet<string>`
3. `EmitCall`：在"伪类静态"分支前插入 StdlibCallIndex 查询
4. 实例调用：先尝试 StdlibCallIndex，找不到才用 VCallInstr
5. `Generate()` 结束后暴露 `UsedStdlibNamespaces` 属性

## BuildCommand 修改要点

1. 构建 StdlibCallIndex 在 `nsMap` 建完后、`CompileFile` 前
2. 仅加载 `kind=lib` 且来自 stdlib 路径（`artifacts/z42/libs/`）的 zpkg
3. `CompileFile` 接受 `StdlibCallIndex stdlibIndex`，传入 `IrGen`
4. 收集 `irModule` 对应 IrGen 的 `UsedStdlibNamespaces`，合并到 `depMap`（覆盖/补充现有 using-based depMap）

## NativeTable 删除

`ValidateNativeMethod` 改为：
- 只检查 `extern` 和 `[Native]` 共存
- 不再验证 intrinsic 名称是否在注册表中（运行时 VM 负责）

## Decisions

### Decision 1：实例方法 CallInstr vs VCallInstr
**问题：** IrGen 在 EmitCall 时无 receiver 的静态类型，无法区分 String.Contains vs List.Contains  
**选项：** A — 传 receiver 类型（需 TypeChecker 输出）；B — 方法名无歧义则 CallInstr，有歧义则 VCallInstr  
**决定：** B，简单且够用；歧义方法（Contains）的 VCallInstr 由 VM 通过 stdlib 类的方法表解析

### Decision 2：依赖不依赖 using 声明
**问题：** 现有示例无 `using z42.io`，删 BuiltinTable 后若依赖 using 则行为破坏  
**选项：** A — 强制 using；B — 追踪 IrGen 实际调用，自动注入依赖  
**决定：** B，向后兼容现有示例

## Testing Strategy
- `dotnet test` — 现有 golden tests 继续通过（IR 输出从 BuiltinInstr → CallInstr，需更新 golden 文件）
- `./scripts/test-vm.sh` — VM 能执行 stdlib stub 中的 CallInstr → BuiltinInstr 链路
