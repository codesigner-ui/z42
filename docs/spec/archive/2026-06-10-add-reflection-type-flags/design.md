# Design: Type.IsAbstract / IsSealed（zbc 1.12）

## Architecture

```
ClassDecl.IsAbstract/IsSealed/IsStruct/IsRecord   (AST, 已有)
        │ IrGen.EmitClassDesc
        ▼
IrClassDesc { IsAbstract, IsSealed, IsStruct, IsRecord }   (z42.IR)
        │ ZbcWriter.BuildTypeSection — append flags:u8 (bit0..3)
        ▼
zbc TYPE section  ──ZbcReader / read_type──►  ClassDesc { class_flags:u8 }
        │ build_type_registry
        ▼
TypeDesc { class_flags:u8 }  ◄── reflection builtins ── Type.IsAbstract / IsSealed
```

## Decisions

### Decision 1: 一个 flags 字节，捕获 4 位，暴露 2 个
**问题**：abstract/sealed 是请求项；struct/record 也在 `ClassDecl` 现成可读。
**决定**：wire 写 `flags:u8`（bit0 abstract / bit1 sealed / bit2 struct / bit3 record）一次捕获 4 位；stdlib 现只暴露 `IsAbstract` / `IsSealed`。**理由**：格式 bump 昂贵（逼 port re-port），一次把类形状标志写全，将来 `IsValueType`/`IsRecord` 纯 stdlib 加 getter、不再 bump。位是 reserved-but-written，非展望式空想（数据真写进 wire）。

### Decision 2: flags 追加在 TYPE 记录末尾（attr 块之后）
**问题**：插在哪？
**决定**：append 到每类记录最末（name→base→fields→tps→attrs→**flags**）。读端在现有读序末尾加一次 `read_u8`，对前序字段零扰动，diff 最小。

### Decision 3: `class_flags` 存 hot `TypeDesc`（1 字节），不进 cold
**问题**：E2.P1 把 5 个稀用字段挪进 cold box 省内存；flags 放哪？
**决定**：放 hot `TypeDesc` 作 `class_flags: u8`。理由：单字节大概率落进现有 padding，零额外分配；放 cold 则普通类（flags=0）cold=None 时虽默认 0 正确，但 abstract/sealed 类要确保 cold 已分配，反而复杂。1 字节不违背 E2.P1（其针对的是 5×16B 的 String/Vec）。

### Decision 4: 不暴露 `Type.IsStatic`
**问题**：deferred note 列了 IsStatic。
**决定**：z42 `ClassDecl` 无 static-class 修饰符（只有 field/method 级 static）→ `Type.IsStatic` 无意义，不加。bit 位留空，将来若引入 `static class` 再加 emit + API。

### Decision 5: 无后向兼容（strict-pin）
zbc 1.11→1.12 后旧 zbc 不可读（pre-1.0 政策）。全量 fixture regen + stdlib regen 是预期，无兼容路径。

## Implementation Notes

- **builtin**：`builtin_type_is_abstract` / `builtin_type_is_sealed`——`type_handle(args)` 取 `TypeDesc`，返 `Value::Bool(td.class_flags & BIT != 0)`；无句柄 → `Value::Bool(false)`（lenient）。位常量集中定义（如 `const CLASS_FLAG_ABSTRACT: u8 = 1<<0` …）于 metadata，writer/reader/builtin 共用语义。
- **ZbcWriter**：`w.Write((byte)flags)` 在 attr 块后；`flags = (abstract?1:0) | (sealed?2:0) | (struct?4:0) | (record?8:0)`。VersionMinor 11→12（注释记本次变更）。
- **read 端对称**：C# ReadTypeSection + Rust read_type 都在 attr 读完后 `ReadByte()` / `read_u8()`，存入 IrClassDesc / ClassDesc。
- **位常量单一真相**：C# 侧与 Rust 侧各定义一份（值必须一致：1/2/4/8），design 此处即权威表。
- **handle-less Type**：`__obj_get_type` 对基础类型/数组造无句柄 Type → builtin 见 None 返 false。

## Testing Strategy

- **Golden** `src/tests/types/type_flags.z42`：`abstract class` / `sealed class` / 普通 class / 子类，`Assert` 校 IsAbstract/IsSealed（局部接收者，规避 chained 派发坑）。
- **Dogfood [Test]** `reflection.z42`：同上，经 z42.test runner。
- **Rust 单测**：lenient（非 Type arg → false）+ flags 解码（构造带 class_flags 的 TypeDesc，验 builtin 返对应 bool）。
- **Fixture**：跑 `generate-fixtures.sh`（zbc + zpkg）regen，git diff 显示 flags 字节 delta + 版本号。
- **GREEN**：dotnet build + cargo build + dotnet test（含 Zbc/Zpkg format invariant + 新 golden）+ cargo test --lib。无 xtask 时以 C# GoldenTests 为权威门（沿用本会话约定，driver-direct 重建 stdlib）。
- **版本 bump 自检**：ZbcWriter/ZpkgWriter VersionMinor + 两个 Rust 常量 + zbc.md/zpkg.md changelog + fixture regen，缺一 FormatInvariantTests 会红。
