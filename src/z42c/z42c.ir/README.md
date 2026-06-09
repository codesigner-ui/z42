# z42c.ir

## 职责
镜像 C# [z42.IR](../../compiler/z42.IR/README.md)：共享契约（IR 内存模型 + zbc 二进制格式 + 项目类型）。与 C# 一致为**无依赖叶子**。CG-1A 起落地寄存器式 SSA **IR 内存模型**（IrModule → IrFunction → IrBlock → IrInstr/IrTerminator）；byte-identical `.zbc`（ZbcWriter）+ token 分配是后续独立 change。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/IrType.z42` | IR 基本类型标签（int 常量：I8..I64/U8..U64/F32/F64/Bool/Char/Str/Ref/Void）+ `Name(tag)`。堆对象塌缩为 Ref。`Z42Type→IrType` 映射不在此（叶子不引用 semantics），在 FunctionEmitter |
| `src/TypedReg.z42` | 带类型标签的虚拟寄存器 `TypedReg(Id, Type)` |
| `src/IrModule.z42` | 容器（叶子优先序）：IrFieldDesc / IrClassDesc / IrBlock / IrFunction / IrModule。集合 = typed array + count；StringPool（1-based）。Dump 出 .zasm-like 文本 |
| `src/IrInstr.z42` | IrInstr 基类 + virtual Dump() + 子类（class-per-instruction）：Const* + Copy + Add/Sub/Mul/Div/Rem + Call/VCall/FieldGet/FieldSet（CG-1C）+ ObjNew/ArrayGet/ArraySet/IsInstance/AsCast（CG-1D）+ Eq..Ge/BitAnd..Shr/Not/Neg/BitNot/StrConcat（CG-1E）。30+ 条，逼近 500 行将按类别拆 |
| `src/IrTerminator.z42` | IrTerminator 基类 + RetTerm/BrTerm/BrCondTerm/ThrowTerm（终结基本块） |
| `src/IrSkeleton.z42` | B0 占位（暂留：SemanticsSkeleton/ProjectSkeleton/PipelineSkeleton 仍引用；随其移除时清理） |

## 入口点
`Z42.IR` 命名空间。IR 由 z42c.semantics 的 IrGen/FunctionEmitter 构建；文本 dump 经 `IrModule.Dump()` / `IrFunction.Dump()`。

## 依赖关系
无（叶子；与 C# z42.IR 一致）。stdlib 自动可用。

## 受限写法
class（非 record）+ virtual Dump 替 record 层次；static class + int 常量替 enum（IrType）；typed array + count 替泛型集合字段。**定义顺序叶子优先**（容器引用叶子的 Dump，bootstrap 单遍按文件序解析，后定义的具体类型方法不可见）。

## 增量进度
CG-1A 最小指令集 ✅ / CG-1B 控制流（Br/BrCond）✅ / CG-1C 调用·字段 ✅ / CG-1D new·数组·is·as ✅ / CG-1E 运算符（比较/位/一元/字符串拼接）✅。后续：CG-1E-2 逻辑短路·三目·??（块化）/ CG-2 泛型。byte-identical .zbc（ZbcWriter）独立 change。
