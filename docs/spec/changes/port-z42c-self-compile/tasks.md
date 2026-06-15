# Tasks: port-z42c-self-compile

> 状态：🟡 进行中 | 创建：2026-06-15

**变更说明：** dogfood gap-batch——让自举 z42c 编译器能编译**自己的源码**（src/z42c/
7 包），逐个补齐 z42c 当前缺的语法/语义/stdlib 缺口（feedback_dogfood_fill_gaps：
缺口在 z42c 里实现，不绕过）。从最小叶子包 z42c.core 起，逐包推进。

**方法：** 用自举 driver（`z42c build <member-src-toml>`）编译各 z42c 包源 → 遇错即定位
缺口 → 在 z42c 实现 → 重建重试。最终目标：z42c byte-identical 编译全部 7 包 = 完整自举。

## 进度（按发现顺序）
- [x] G1 `_isVarDeclStart`：识别 `Name[] var`（用户类数组类型局部声明）。**z42c.core 全包自编译通过**（无错）。
- [x] G2 `_bindMember`：prim receiver 属性访问（`s.Length`/`s.ByteLength` 等）镜像 `_bindMemberCall` 的 prim→stdlib 包装类映射；查无松绑 Unknown。z42c.ir/ByteWriter.z42 触发。
- [x] G3 C 风格强制转换 `(Type)expr`（`(int)`/`(byte)`/`(long)` 等类型关键字 cast）。全流程：CastExpr AST + parser cast 消歧（`( 类型关键字 )` 无歧义）+ BoundConvert + ConvertInstr（op 0xB1，镜像 C# ConvertInstr）+ ExprEmitter `_emitConvert`（镜像 C# VisitCast no-op 规则）+ REGT visit。
- [x] G4 整型字面量 radix 解析（`0xFF`/`0b..`/`_` 分隔/尾后缀）：`ZbcInstr._parseIntLit`（统一 `_parseRadix`，镜像 C# ParseIntLit）替代裸 `Convert.ToInt32/64`（仅解十进制）。**z42c.ir 全包自编译通过**。
- [x] G5 基本类型别名等价（`byte[]`≡`u8[]`）：`Z42Type.Canon`（剥 nullable `?` + prim 别名归一）。z42c.project/ZpkgBuilder 触发。
- [x] G6 数组元素 unknown/error 吸收（`IrXxx[]`→`<unknown>[]`，imported 元素类型在字段位退化）：Z42ArrayType.IsAssignableTo 吸收。z42c.semantics/EmitContext 触发。
- [x] G7 nullable 规范化（`string?`→`string`）：Canon 剥 `?` → Prim 可赋性等价。z42c.driver/Main 触发。
- [x] G8 无初始化器局部声明 `Type name;`：parser init 可选 + typecheck 跳赋值检查 + codegen 无 init 不发 IR（变量首赋值绑定，镜像 C# VisitVarDecl）+ Dump null 守卫。z42c.semantics/ExportedTypeExtractor 触发。

## 🎉 里程碑：z42c 自编译全部 7 个自身包（功能性自举）
G1-G8 后 **`z42c build` 编译 z42c.{core,ir,syntax,project,semantics,pipeline,driver} 全部 7/7 无错**。
下一级：逐包 byte-identical 对账（z42c 产物 vs C# CLI；workspace 默认 [sources] + 跨包 namespace/版本上下文对齐）。

## 验证
- [ ] 每清一个缺口：`xtask test compiler-z42` 保持全绿（不回归 byte-compare 7/7 + zpkg 6/6）
- [ ] 里程碑：z42c 能编译 z42c.core 全部源（无 error）

## 备注
- 占用 z42c 锁（延续自举主线）。byte-identical 对账留各包能编译后再逐包验。
- workspace 约定默认 `[sources]`（成员无 [sources] 段时）= 单独的 driver/project 缺口，暂用显式 toml 绕过，后续补 workspace build 编排。
