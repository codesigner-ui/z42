# Tasks: 隐式 Object 继承

> 状态：🟢 已完成 | 创建：2026-04-13

## 进度概览
- [x] 阶段 1: VM corelib — Object builtin 已存在
- [x] 阶段 2: VM — JIT field_get 虚字段修复
- [x] 阶段 3: 编译器 — TypeChecker 隐式基类 + IrGen 输出
- [x] 阶段 4: 测试与验证

## 阶段 1: VM corelib（已有）
- [x] 1.1 corelib/object.rs: `builtin_obj_equals`、`builtin_obj_hash_code`、`builtin_obj_to_str` 已实现
- [x] 1.2 corelib/mod.rs: `__obj_equals`、`__obj_hash_code`、`__obj_to_str` 已注册

## 阶段 2: VM 修复
- [x] 2.1 jit/helpers.rs: `jit_field_get` 增加虚字段分发（Str.Length、Array.Length/Count、Map.Length/Count）

## 阶段 3: 编译器
- [x] 3.1 TypeChecker: 预注册 Object 类型（含 ToString/Equals/GetHashCode 虚方法签名）
- [x] 3.2 TypeChecker: 无显式基类的 class（非 struct/record/Object）自动设 BaseClass = "Object"
- [x] 3.3 TypeChecker: override 验证同时检查接口方法
- [x] 3.4 IrGen: EmitClassDesc 为隐式继承的 class 输出 base_class = "Std.Object"
- [x] 3.5 Z42Type: 添加 ClassType → object 赋值兼容规则

## 阶段 4: 测试与验证
- [x] 4.1 golden test 51_implicit_object_base: 默认 ToString + override ToString + Equals + GetHashCode
- [x] 4.2 dotnet build && cargo build 成功
- [x] 4.3 dotnet test: 386 passed
- [x] 4.4 ./scripts/test-vm.sh: 96 passed（interp 48 + jit 48）
- [x] 4.5 docs/design/language-overview.md 更新（隐式 Object 继承机制描述）
