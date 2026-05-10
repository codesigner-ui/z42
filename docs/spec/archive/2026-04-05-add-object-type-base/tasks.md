# Tasks: Add Object Base Class and Type Descriptor

> 状态：🟢 已完成 | 创建：2026-04-05 | 完成：2026-04-05

## 进度概览
- [x] 阶段 1: stdlib (.z42 文件)
- [x] 阶段 2: 编译器 NativeTable
- [x] 阶段 3: VM builtins
- [x] 阶段 4: 测试与验证

## 阶段 1: stdlib
- [x] 1.1 更新 Object.z42：GetType(), ReferenceEquals(), Equals(Object?), GetHashCode() native, ToString()
- [x] 1.2 新建 Type.z42：sealed class，__name/__fullName 私有字段，Name/FullName 属性

## 阶段 2: 编译器
- [x] 2.1 NativeTable.cs：添加 __obj_get_type(1), __obj_ref_eq(2), __obj_hash_code(1)

## 阶段 3: VM builtins
- [x] 3.1 builtins.rs dispatch_table：注册三个新 builtin
- [x] 3.2 builtin_obj_get_type：提取 class_name，构建 Type 对象
- [x] 3.3 builtin_obj_ref_eq：Rc::ptr_eq + null 处理
- [x] 3.4 builtin_obj_hash_code：Rc::as_ptr() 地址 → i32

## 阶段 4: 验证
- [x] 4.1 dotnet build —— 无编译错误
- [x] 4.2 cargo build —— 无编译错误
- [x] 4.3 dotnet test —— 全绿
- [x] 4.4 ./scripts/test-vm.sh —— 全绿
- [x] 4.5 docs/design/language-overview.md 更新：Object 协议、Type 类
- [x] 4.6 docs/roadmap.md 更新：Object/Type 特性进度

## 备注
