# 编译器构建（C# Bootstrap）

z42 编译器 (`z42c`) 是 .NET 项目，源码在 [`src/compiler/`](../../../src/compiler/)。

## 前置

- .NET 10 SDK
- macOS / Linux / Windows 任一

## 标准构建

```bash
dotnet build src/compiler/z42.slnx              # 默认 Debug
dotnet build src/compiler/z42.slnx -c Release   # Release
```

产物：`artifacts/build/compiler/<project>/bin/<config>/net10.0/<proj>.dll`。`z42c.dll` 是 driver 入口。

## 编译器入口（`z42c`）

### 单文件模式

```bash
dotnet run --project src/compiler/z42.Driver -- <file.z42> [--emit ir|zbc] [-o <out>]
```

| Flag | 用途 |
|------|------|
| `--emit zbc` | 产出 `.zbc` 字节码（VM 可直接执行） |
| `--emit ir` | 产出 `.zasm` 文本（调试查看 IR） |
| `-o <out>` | 输出路径 |

### 项目模式（`<name>.z42.toml`）

```bash
dotnet run --project src/compiler/z42.Driver -- build [<name>.z42.toml] [--release] [--bin <name>]
dotnet run --project src/compiler/z42.Driver -- check [<name>.z42.toml] [--bin <name>]
dotnet run --project src/compiler/z42.Driver -- run   [<name>.z42.toml] [--release] [--bin <name>] [--mode interp|jit|aot]
dotnet run --project src/compiler/z42.Driver -- clean [<name>.z42.toml]
```

manifest 字段定义见 [`docs/design/compiler/project.md`](../../design/compiler/project.md)；增量编译机制见 manifest 内 `[build]` + cache 行为说明。

### 工具命令

```bash
dotnet run --project src/compiler/z42.Driver -- disasm <file.zbc> [-o <file.zasm>]   # 反汇编 zbc → zasm 文本
dotnet run --project src/compiler/z42.Driver -- explain <ERROR_CODE>                 # E#### / W#### / WS### 详细说明
dotnet run --project src/compiler/z42.Driver -- errors                               # 列全部错误码
```

## 分发版 binary

把 dotnet 单文件 binary 打到 `artifacts/build/runtime/release/`：

```bash
z42 xtask.zpkg build package debug     # debug
z42 xtask.zpkg build package release   # release
```

之后 `z42c` 可独立运行（无需 `dotnet run`）。详见 [`stdlib.md`](stdlib.md) 关于 stdlib 同步的描述。

## 单元测试

见 [`../testing/unit-tests.md`](../testing/unit-tests.md)。
