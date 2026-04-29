# Tasks: Class-level `[Native]` defaults (C9)

> 状态：🟢 已完成 | 完成：2026-04-29 | 创建：2026-04-29

## 阶段 1: AST nullable + ClassDecl

- [x] 1.1 `Tier1NativeBinding` 字段改 nullable (`string?`)
- [x] 1.2 `ClassDecl` 加 `Tier1NativeBinding? ClassNativeDefaults = null` 默认参数

## 阶段 2: Parser partial + class-level

- [x] 2.1 `TryParseNativeAttribute`：接受 partial Tier1 form（lib/type/entry 任意子集 ≥ 1）
- [x] 2.2 `TopLevelParser.cs::ParseCompilationUnit`：把 pendingNative 传给 ParseClassDecl
- [x] 2.3 `ParseClassDecl` 接 `NativeAttribute? classNative` 参数；提取 Tier1 → ClassNativeDefaults
- [x] 2.4 同时 ClassDecl 构造器透传

## 阶段 3: TypeChecker 拼接验证

- [x] 3.1 `ValidateNativeMethod` 接收 class context；计算 stitched binding（method 优先）
- [x] 3.2 缺任何字段 → E0907

## 阶段 4: IrGen 拼接

- [x] 4.1 `EmitNativeStub` 接 `Tier1NativeBinding? classDefaults`
- [x] 4.2 拼接后 emit CallNativeInstr

## 阶段 5: 测试

- [x] 5.1 `NativeAttributeTier1Tests.cs` 加 5 个新测试

## 阶段 6: 文档 + GREEN + 归档

- [x] 6.1 error-codes.md E0907 抛出条件加 "stitched incomplete"
- [x] 6.2 interop.md / roadmap.md 加 C9 行
- [x] 6.3 GREEN
- [x] 6.4 归档 + commit + push
