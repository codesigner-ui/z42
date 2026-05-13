# Tasks: Add z42.encoding

> 状态：🟢 已完成 | 创建/完成：2026-05-13
> 类型：lang（完整流程；纯脚本新包，无 IR/VM 改动）

## 实施备注

- **43/43 测试通过**：Hex 11 / Base64 17 / UTF-8 15
- **静态字段 quirk**：发现 stdlib 包跨包 `private static string FOO = "..."` 在外部首次触发时为 Null。workaround = 方法内局部变量（见 Hex.z42 / Base64.z42 注释）。根本修复独立 spec follow-up，已记入 roadmap Deferred Backlog Index
- **byte literal cast**：高位 byte 字面量（如 `0xAB`）赋给 byte[] 元素需显式 `(byte)` 前缀；测试用例已修
- 工程集成：workspace.toml + build-stdlib.sh + index.json 同步加 `Std.Encoding → z42.encoding.zpkg`
- 文档落地：encoding.md 完整设计 + roadmap.md P0 表移除 + Deferred Backlog Index +2 项

## 验证报告

### 测试通过
- z42.encoding 43/43 ✅
- 跨包回归：build-stdlib 7/7 ✅
- z42.math / z42.text / z42.test / z42.io（无 add-std-process Z42IoProcess*）保持原状

### Spec 覆盖

所有 Requirement 通过单元测试端到端验证：
- Hex Encode + EncodeUpper + Decode + 错误条件 + 往返
- Base64 RFC 4648 §10 全 7 vectors × 2 方向 + 错误条件 + 二进制往返
- UTF-8 4 长度类别 × 2 方向 + 截断 / overlong / surrogate / 非法首字节 + 混合往返

## 进度概览

- [x] 阶段 1: 包 manifest + README
- [x] 阶段 2: 验证 FormatException 现状（若缺加最小骨架）
- [x] 阶段 3: Hex.z42 实现
- [x] 阶段 4: Base64.z42 实现
- [x] 阶段 5: Utf8.z42 实现
- [x] 阶段 6: 单元测试（Hex / Base64 / Utf8 三文件）
- [x] 阶段 7: 工程集成（workspace.toml + build-stdlib.sh + index.json）
- [x] 阶段 8: 文档同步
- [x] 阶段 9: GREEN + 归档

## 阶段 1: 包 manifest + README

- [x] 1.1 [src/libraries/z42.encoding/z42.encoding.z42.toml](../../../../src/libraries/z42.encoding/z42.encoding.z42.toml) NEW — manifest，depend on `z42.core`
- [x] 1.2 [src/libraries/z42.encoding/README.md](../../../../src/libraries/z42.encoding/README.md) NEW — 包目录 README

## 阶段 2: FormatException 验证

- [x] 2.1 检查 `src/libraries/z42.core/src/Exceptions/` 是否已有 `FormatException.z42`
- [x] 2.2 若缺 — 新建最小骨架（继承 `Std.Exception`，单 `string message` ctor）；若有 — 直接 reference

## 阶段 3: Hex.z42 实现

- [x] 3.1 [src/libraries/z42.encoding/src/Hex.z42](../../../../src/libraries/z42.encoding/src/Hex.z42) NEW
- [x] 3.2 `Encode(byte[]) → string` + `EncodeUpper(byte[]) → string` + 共享 `encodeWith(bytes, alpha)`
- [x] 3.3 `Decode(string) → byte[]` 含奇数长度 / 非法字符 校验
- [x] 3.4 私有 `digitValue(char) → int` 助手

## 阶段 4: Base64.z42 实现

- [x] 4.1 [src/libraries/z42.encoding/src/Base64.z42](../../../../src/libraries/z42.encoding/src/Base64.z42) NEW
- [x] 4.2 `Encode(byte[]) → string`：3-byte 块循环 + padding 处理
- [x] 4.3 `Decode(string) → byte[]`：反向查表（`byte[256]` lookup，0xFF 哨兵），4-char 块循环 + padding 校验
- [x] 4.4 错误：非法字符 / 长度错误 / 内部 padding 抛 `FormatException`

## 阶段 5: Utf8.z42 实现

- [x] 5.1 [src/libraries/z42.encoding/src/Utf8.z42](../../../../src/libraries/z42.encoding/src/Utf8.z42) NEW
- [x] 5.2 `GetBytes(string)`：两遍扫描（算总字节数 → 分配 → 填充）
- [x] 5.3 `GetString(byte[])`：滚动 byte index，每个序列长度判定 + 续字节读取 + codepoint 拼装 + char 输出
- [x] 5.4 严格校验：overlong / surrogate (U+D800-U+DFFF) / 超界 (>U+10FFFF) / 截断 / 非法首字节 抛 `FormatException`

## 阶段 6: 单元测试

- [x] 6.1 [tests/hex.z42](../../../../src/libraries/z42.encoding/tests/hex.z42) NEW — ≥10 case 覆盖 spec
- [x] 6.2 [tests/base64.z42](../../../../src/libraries/z42.encoding/tests/base64.z42) NEW — RFC 4648 §10 全部 7 vectors × 2 方向 + 错误条件 = ≥15 case
- [x] 6.3 [tests/utf8.z42](../../../../src/libraries/z42.encoding/tests/utf8.z42) NEW — 4 长度类别 × 2 方向 + 截断 / overlong / 混合往返 = ≥12 case

## 阶段 7: 工程集成

- [x] 7.1 [src/libraries/z42.workspace.toml](../../../../src/libraries/z42.workspace.toml) — `default-members` 加 `z42.encoding`
- [x] 7.2 [scripts/build-stdlib.sh](../../../../scripts/build-stdlib.sh) — `LIBS=(...)` 加 `z42.encoding` + `index.json` heredoc 加 `"Std.Encoding": "z42.encoding.zpkg"`
- [x] 7.3 build 验证：`./scripts/build-stdlib.sh` 7 → 8 库全绿

## 阶段 8: 文档同步

- [x] 8.1 [docs/design/stdlib/encoding.md](../../../design/stdlib/encoding.md) NEW — 包设计文档：API 矩阵 / 决策记录 / RFC 引用 / Deferred 段（URL-safe Base64 / Base32 / UTF-16 / streaming / performance native）
- [x] 8.2 [src/libraries/README.md](../../../../src/libraries/README.md) — 包列表加 z42.encoding 行
- [x] 8.3 [docs/design/stdlib/roadmap.md](../../../design/stdlib/roadmap.md) — P0 表移除 z42.encoding 行 + Deferred Backlog Index 加 URL-safe Base64 等
- [x] 8.4 [docs/design/stdlib/organization.md](../../../design/stdlib/organization.md) — 现状包列表加 z42.encoding

## 阶段 9: GREEN + 归档

- [x] 9.1 ./scripts/test-stdlib.sh z42.encoding — 全绿
- [x] 9.2 ./scripts/test-all.sh — 除 add-std-process pre-existing 阻塞外全绿
- [x] 9.3 spec scenarios 逐条覆盖确认
- [x] 9.4 mv `docs/spec/changes/add-z42-encoding/` → `docs/spec/archive/2026-05-13-add-z42-encoding/`
- [x] 9.5 commit + push

## 备注

- 实施期 Decision 3 verify：若 `FormatException` 不存在则本 spec 同时落地最小骨架；不另开 spec
- Base64 反向查表用 256-element `byte[]` 一次初始化（懒加载或 static field）；如 z42 当前 static field 初始化对 byte[] 有限制，回退用懒生成 (call-once) helper
- 性能：MVP 不做 SIMD / 表预计算 outside object；JIT 编译后 byte loop 性能可接受
