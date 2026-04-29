# Proposal: Class-level `[Native]` defaults (C9)

## Why

C6 让 z42 用户代码能用 `[Native(lib=, type=, entry=)]` 调 native 函数，但**每个方法都要重复 lib + type**：

```z42
public static class NumZ42 {
    [Native(lib="numz42", type="Counter", entry="__alloc__")]
    public static extern long Alloc();

    [Native(lib="numz42", type="Counter", entry="inc")]
    public static extern long Inc(long ptr);

    [Native(lib="numz42", type="Counter", entry="get")]
    public static extern long Get(long ptr);
}
```

对任何非平凡 native 库这都是噪声。C9 允许把 `lib` + `type` 放到**类级**：

```z42
[Native(lib="numz42", type="Counter")]
public static class NumZ42 {
    [Native(entry="__alloc__")] public static extern long Alloc();
    [Native(entry="inc")]       public static extern long Inc(long ptr);
    [Native(entry="get")]       public static extern long Get(long ptr);
}
```

方法只需声明 `entry`；编译器把类级默认 + 方法 entry 拼接成完整 `Tier1NativeBinding`。

## What Changes

- **`Tier1NativeBinding`** 字段改为 nullable（`string?`）以承载部分信息（C9 之前所有调用点假定全部非 null —— 同步更新）
- **`ClassDecl`** 加可选 `Tier1NativeBinding? ClassNativeDefaults` 字段
- **Parser**：
  - `TryParseNativeAttribute` 接受 lib/type/entry 任意子集（不再强制三个全有）
  - 类前的 `[Native(...)]` 走类级 default 路径（当前 `pendingNative` 机制已对应到 ClassDecl 调用点）
  - 方法级仍可全形式（C6 兼容）
- **TypeChecker**：`ValidateNativeMethod` 把方法绑定与类级 defaults 拼接，要求最终 (lib, type, entry) 全部非 null；缺失任一 → E0907
- **IrGen**：`EmitNativeStub` 接 `ClassNativeDefaults`，按需补齐方法绑定；emit `CallNativeInstr(stitched.Lib, stitched.TypeName, stitched.Entry, args)`
- **测试**：parser / typecheck / codegen 各 1-2 case

## Scope

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | `Tier1NativeBinding` 字段改 nullable；`ClassDecl` 加 `ClassNativeDefaults` |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | `TryParseNativeAttribute` 接受 partial Tier1 form；同时校验"只 entry / 只 lib+type / 三键全" |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.cs` | MODIFY | 调用 `ParseClassDecl` 时把 `pendingNative.Tier1` 传作 class defaults（如果是 lib+type 形式）|
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs` | MODIFY | `ParseClassDecl` 接受 `Tier1NativeBinding? classDefaults`；存到 ClassDecl |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | MODIFY | stitched binding 校验（lib/type/entry 全部非 null） |
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | `EmitNativeStub` 接 ClassDefaults；方法 Tier1Binding 与 class defaults 拼接 |
| `src/compiler/z42.Tests/NativeAttributeTier1Tests.cs` | MODIFY | 加 4 个 case：class+method partial 拼接 / class 缺 lib 报错 / method full 仍工作 / class defaults 不写时旧 C6 路径继续工作 |
| `docs/design/error-codes.md` | MODIFY | E0907 抛出条件加"stitched binding incomplete" |
| `docs/design/interop.md` | MODIFY | §10 加 C9 行 ✅ |
| `docs/roadmap.md` | MODIFY | C9 → ✅ |

## Out of Scope

- `extern class T { ... }` 关键字 / declarative 形式（只是 visibility 标记，独立 spec）
- Method 仅 lib 或仅 type（不带 entry）—— 始终 E0907；entry 必须由方法或类级中至少有一处提供且最终拼接出完整 entry

## Open Questions

- [ ] **Q1**：方法级 [Native] 给出 lib/type 与 class defaults 不同时，谁赢？
  - 倾向：**方法级覆盖类级**（局部更具体）。如 class 给 lib=A, method 给 lib=B → 用 B
- [ ] **Q2**：类级 [Native(entry=...)] 算合法吗？
  - 倾向：**否**，class entry 没意义（类不是单一函数），E0907
