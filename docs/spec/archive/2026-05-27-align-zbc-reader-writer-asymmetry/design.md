# Design: Align zbc reader-writer asymmetry (Option A)

## Decision provenance

`docs/design/runtime/zbc.md` Deferred §reader-writer-asymmetry 列了三个选项：

| 选项 | 描述 | 代价 |
|------|------|------|
| A | SIGS / TYPE 加 `retType_str_idx` / `field_type_str_idx`（既 TypeTag 又字符串）| zbc minor bump + fixture regen |
| B | 编译期 normalize 用户类型名到 canonical（`"int"` → `"i32"` 编译时统一）| UX 倒退（错误消息从 `"int"` 变 `"i32"`） |
| C | 永不修 | 0 代价；CI 防线缺失 |

**2026-05-14 决策**：C。
**2026-05-27 决策**（本 spec）：**Option A**。User override，理由：proactive 补 CI 防线，趁 stdlib 不大，越往后越贵。B 永不再考虑（UX 倒退）。

## Architecture

```
Writer side (C#)
─────────────────
ZbcWriter.BuildSigsSection:
  per function:
    name_idx (u32)
    param_count (u16)
    ret_tag (u8)              ← existing
    ret_type_str_idx (u32)    ← ✨ NEW
    exec_mode (u8)
    is_static (u8)
    param_types[]: u32×param_count
    tp_count + type_params + constraints

ZbcWriter.BuildTypeSection:
  per class:
    name_idx, base_idx, fld_count
    per field:
      fnam_idx (u32)
      type_tag (u8)             ← existing
      field_type_str_idx (u32)  ← ✨ NEW
    tp_count + type_params + constraints

Reader side (Rust)
───────────────────
read_sigs: 同 layout 反序列化，用 ret_type_str_idx → pool.lookup → FuncSig.ret_type
           （type_tag 仍解码但仅作 sanity check / fallback；优先 string）
read_type: 同 layout 反序列化，用 field_type_str_idx → pool.lookup → FieldDesc.type_tag
```

## Decisions

### Decision 1: TypeTag u8 不删除

**问题**：现在 string 是权威，那 1 byte tag 是冗余还是有用？

**决定**：保留。理由：
- disasm / debug 输出可直接读 tag 字典分类（不需查池）
- 未来 JIT 快路径可能直接用 tag 选 CLIF 操作（不查池）
- 字段宽度 +5 bytes/func + 5 bytes/field 已经在可接受范围内；额外 -1 byte 收益不大
- 删 tag 需要同步 IR / disasm / spec 更多面，scope 膨胀

### Decision 2: Reader 优先 string，忽略 tag mismatch

**问题**：若 tag 和 string 描述了不同类型（合法 zbc 1.7 不该出现，但 corrupt 文件可能）— 怎么处理？

**决定**：reader **以 string 为准**，不做 tag-vs-string consistency check（防御性 check 留作 future linter，不进 hot path）。理由：
- string 是权威；tag 仅 hint
- consistency check 增 cost 又没明确防御场景
- 真正 corrupt 文件该被早期 magic/version/section 检查拦下

### Decision 3: zpkg 联动 bump

按 `.claude/rules/version-bumping.md`：zbc minor bump 必须同步 zpkg minor bump。所以 zpkg 0.7 → 0.8。

### Decision 4: Stripped mode (`.cache/*.zbc`) 不写 SIGS/TYPE

Stripped flag 表示"缺 STRS/TYPE/SIGS/EXPT/IMPT"。所以 stripped mode 不受 wire format 变化影响（不写就不需要新字段）。

### Decision 5: 调整 ParamTypes lossy 同源问题？

**问题**：design doc 也提了 paramTypes 的 lossy。要不要同 spec 修？

**答**：不需要 — paramTypes **已经是字符串**（1.3 split-debug-symbols 起，u32 strIdx × ParamCount）。只 retType / fld.type 是 1 byte tag-only。本 spec scope 只覆盖这两处。

### Decision 6: ReadWriteRoundTrip CI test 位置

**决定**：放 `src/compiler/z42.Tests/Zbc/ReadWriteRoundTripTests.cs`（C# / dotnet test），不放 Rust。理由：
- writer 是 C# (`ZbcWriter`)，reader 是 Rust (`zbc_reader.rs`)
- C# 测试可以最直接闭环：read existing fixture bytes → write back → assert equal
- 实际上 C# 端有自己的 `ZbcReader.cs` 也参与 round-trip （compiler 自己也读 zbc — 增量编译 cache）；测 C# round-trip 同时也测 C# reader（Rust reader 由 fixture golden 测）

## Implementation Notes

### ZbcWriter.cs diff（SIGS）

```diff
 w.Write((uint)pool.Idx(fn.Name));
 w.Write((ushort)fn.ParamCount);
 w.Write(TypeTags.FromString(fn.RetType));
+w.Write((uint)pool.Idx(fn.RetType));   // 1.7: explicit ret type string
 w.Write(ExecModes.FromString(fn.ExecMode));
 w.Write((byte)(fn.IsStatic ? 1 : 0));
```

### ZbcWriter.cs diff（TYPE）

```diff
 w.Write((uint)pool.Idx(fld.Name));
 w.Write(TypeTags.FromString(fld.Type));
+w.Write((uint)pool.Idx(fld.Type));   // 1.7: explicit field type string
```

### zbc_reader.rs diff（read_sigs）

```diff
 let name_idx    = c.read_u32()?;
 let param_count = c.read_u16()? as usize;
 let ret_tag     = c.read_u8()?;
+let ret_type_idx = c.read_u32()?;   // 1.7
 let mode_byte   = c.read_u8()?;
 ...
-ret_type: type_tag_to_str(ret_tag).to_owned(),
+ret_type: c.pool_str(pool, ret_type_idx)?.to_owned(),
```

### zbc_reader.rs diff（read_type）

```diff
 let fnam_idx = c.read_u32()?;
 let type_tag = c.read_u8()?;
+let type_str_idx = c.read_u32()?;   // 1.7
 fields.push(FieldDesc {
     name: c.pool_str(pool, fnam_idx)?.to_owned(),
-    type_tag: type_tag_to_str(type_tag).to_owned(),
+    type_tag: c.pool_str(pool, type_str_idx)?.to_owned(),
 });
```

### Version bumps

```diff
 # src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs
-public const ushort VersionMinor = 6;
+public const ushort VersionMinor = 7;   // 2026-05-27 align-zbc-reader-writer-asymmetry: SIGS/TYPE 加 ret_type/field_type str_idx (read→write byte parity)

 # src/compiler/z42.Project/ZpkgWriter.cs
-public const ushort VersionMinor = 7;
+public const ushort VersionMinor = 8;   // 2026-05-27 align-zbc-reader-writer-asymmetry: inner zbc 1.7

 # src/runtime/src/metadata/zbc_reader.rs
-pub const ZBC_VERSION_MINOR: u16 = 6;
+pub const ZBC_VERSION_MINOR: u16 = 7;
-pub const ZPKG_VERSION_MINOR: u16 = 7;
+pub const ZPKG_VERSION_MINOR: u16 = 8;
```

### Reader-side: 同时也改 C# ZbcReader

Compiler 自己读 zbc（incremental cache）。C# `ZbcReader.cs` 也要同步消费新字段。

## Testing Strategy

- **Fixture regen + golden**：跑 `src/tests/zbc-format/generate-fixtures.sh` + `src/tests/zpkg-format/generate-fixtures.sh`；6 zbc + 4 zpkg fixture bytes 全部刷新；CI 通过格式 invariant 测试
- **Stdlib regen**：`./scripts/regen-golden-tests.sh` 把所有 stdlib zpkg 重生（旧 zbc 1.6 不可加载）
- **NEW: ReadWriteRoundTripTests.cs**：C# 测试加载每个 fixture bytes → ZbcReader → ZbcWriter → 比较字节相等
- **GREEN**: `./scripts/test-all.sh --scope=full` 全绿

## 文档同步

- `docs/design/runtime/zbc.md`：changelog +1.7 行；Deferred 删 reader-writer-asymmetry；header "当前 6" → "当前 7"
- `docs/design/runtime/zpkg.md`：changelog +0.8 行；Deferred 删 reader-writer-asymmetry
- `docs/roadmap.md`：Deferred Backlog Index 删 reader-writer-asymmetry 行
