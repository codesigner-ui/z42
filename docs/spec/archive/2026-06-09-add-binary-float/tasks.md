# Tasks: add-binary-float

> 状态：🟢 完成 | 创建：2026-06-09 | 类型：feat (runtime intrinsic + stdlib)
> 模式：minimal —— 4 个 corelib bit-cast builtin + BinaryReader/Writer float 方法，无 lang/ir。
> 子系统锁：`runtime` + `stdlib`（与 add-reflection-mvp 例外共存，文件/区段分离，见 ACTIVE.md）。

## 背景

`io-binary-future-float-double`：BinaryReader/Writer 此前无 IEEE-754 float 序列化。
许多二进制协议 / 文件格式（gzip frame header、network protocols、game saves）需要
float。设计明确依赖 bit-level reinterpret，z42 无纯脚本实现（BitConverter gap）。

## 设计（对齐 BinaryWriter 既有"宽入窄线"约定）

`WriteInt16LE(int)` 取宽类型序列化到窄宽度 → float 同理：
- **corelib intrinsic**（`convert.rs`，4 个）：`__single_to_bits(f64)→i32`（`(a as f32).to_bits()`）/
  `__single_from_bits(i32)→f64`（`f32::from_bits(bits as u32) as f64`）/ `__double_to_bits(f64)→i64`
  （`a.to_bits()`）/ `__double_from_bits(i64)→f64`。注册落 mod.rs convert 区（远离 reflection 区）。
- **stdlib**（`z42.io.binary`）：`BinaryWriter.WriteSingle{LE,BE}(double)` /
  `WriteDouble{LE,BE}(double)`，复用 `WriteInt32/64{LE,BE}`；`BinaryReader.ReadSingle{LE,BE}()
  → double` / `ReadDouble{LE,BE}() → double`。Single 取/返 `double`（宽浮点），线缆 4-byte f32；
  Double 精确 8-byte f64。

## 任务

- [x] 1. convert.rs：4 个 bit-cast builtin + mod.rs convert 区注册
- [x] 2. z42.io.binary：BinaryWriter Write{Single,Double}{LE,BE} + BinaryReader Read*；私有 extern
- [x] 3. 测试 `tests/binary_float.z42`（8）：1.0f/1.0 已知字节模式 LE/BE · f32-exact + f64
      round-trip · 0.1 narrowing 稳定 · 与整数序列化混合组合
- [x] 4. 文档：io-binary.md `io-binary-future-float-double` → ✅ landed
- [x] 5. GREEN：**canonical 全 gate**（z42.core 现绿 → `test lib z42.io.binary`：build-stdlib
      22/22 + cargo runtime+test-runner 重编 + **6/6 文件全过含 binary_float 8/8**）
- [x] 6. commit（mod.rs `git apply --cached` 单 hunk）+ push + 释锁归档

## 备注

Single 在 z42 以 f64 承载（z42 无独立 f32 运行期值），4-byte f32 仅是线缆格式 —— 与 int
narrow 类型一样（VM 全存 i64，窄宽度只在序列化时体现）。这次 z42.core 已绿,用了真 canonical
gate（非隔离）验证。
