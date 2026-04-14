# z42.IR — 共享契约层

## 职责

编译器与运行时之间的公共数据契约：IR 模型、包格式类型、项目清单类型。
自身不依赖任何其他 z42 项目，被 z42.Compiler、z42.Project、z42.Driver 共同引用。

## 核心文件

| 文件 | 职责 |
|------|------|
| `IrModule.cs` | 编译器输出 / VM 输入的核心 IR 数据结构（SSA 寄存器形式） |
| `WellKnownNames.cs` | 跨编译阶段共享的知名名称判断（如 `IsObjectClass`） |
| `PackageTypes.cs` | 编译产物数据模型：`ZbcFile`、`ZpkgFile`、`ZpkgKind`、`ZpkgMode` |
| `NativeTable.cs` | Builtin 函数名枚举与查表工具 |

### BinaryFormat/
| 文件 | 职责 |
|------|------|
| `Opcodes.cs` | 字节码指令集定义（`0x00`–`0x8F`，约 40 条） |
| `ZbcWriter.cs` | `IrModule` → `.zbc` 二进制序列化 |
| `ZbcReader.cs` | `.zbc` → `IrModule` 反序列化 |
| `ZasmWriter.cs` | `IrModule` → `.zasm` 文本汇编（调试用） |

## 入口点

- `IrModule` — 所有编译器输出和 VM 加载的根类型
- `ZpkgFile` / `ZbcFile` — 包格式根类型
- `Z42Proj` / `Z42Sln` — TOML 项目清单反序列化目标（供 z42.Project 使用）
- `ZbcWriter.Write` / `ZbcReader.Read` — 字节码序列化/反序列化

## 依赖关系

无内部依赖，是整个编译器栈的最底层。
