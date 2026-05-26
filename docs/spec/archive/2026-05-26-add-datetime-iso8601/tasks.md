# Tasks: DateTime.ToIso8601 — UTC ISO-8601 string formatting

> 状态：🟢 已完成 | 创建：2026-05-26 | 归档：2026-05-26

**变更说明：** 给 `Std.Time.DateTime` 添加两个 ISO-8601 格式化方法：
- `ToIso8601()` → `"YYYY-MM-DDTHH:MM:SS.sssZ"`（带毫秒、UTC `Z` 后缀）
- `ToIso8601Basic()` → `"YYYY-MM-DDTHH:MM:SSZ"`（不带毫秒）

**原因：** 把 `scripts/_lib/package_helpers.sh::pkg_emit_manifest` 等脚本移植到 z42 时，`manifest.toml` 里 build-date 字段（bash 现用 `date -u +%Y-%m-%dT%H:%M:%SZ`）需要等价 API。`DateTime.ToString()` 当前只 emit unix ms 整数，无法直接当作 ISO 时间戳。

**类型：** 最小化（pure z42 stdlib 扩展，无新 native，无新 IR/VM 语义）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.time/src/DateTime.z42` | MODIFY | 加 `ToIso8601()` + `ToIso8601Basic()` + 内部 `_civilFromDays` / `_pad2` / `_pad3` / `_pad4` helpers |
| `src/libraries/z42.time/tests/datetime.z42` | MODIFY | 6+ [Test]：epoch、已知历史日期、闰年 2024-02-29、世纪交界 1900-01-01 / 2000-01-01、毫秒精度、basic 版本 |

**只读引用：**
- `src/libraries/z42.time/src/TimeSpan.z42` — 复用同 namespace 风格
- `scripts/_lib/package_helpers.sh:418` — 验证 `date -u +%Y-%m-%dT%H:%M:%SZ` 形态匹配

## 文档影响
- z42.time README：是（新公开 API）
- design doc：不需要新建（实现细节属于内部算法）

## Tasks

- [x] 1.1 在 `DateTime.z42` 末尾加 `ToIso8601()` + `ToIso8601Basic()` 公开方法。
- [x] 1.2 加私有 helper：`_civilFromDays` / `_floorDiv` / `_floorMod` / `_pad2` / `_pad3` / `_pad4` / `_formatIsoDate` / `_formatIsoTimeOfDay`。
- [x] 1.3 9 个新测试全绿（epoch / 2023-11-14 / ms padding / 2024-02-29 闰日 / 2000-01-01 世纪闰年 / 负 unixMs / 1900-01-01 非闰世纪 / 0001-01-01 4-digit padding / 2026-05-26 综合）。
- [x] 1.4 更新 `z42.time/README.md`（API 表 + 改 Deferred 段 strikethrough）。
- [x] 1.5 `./scripts/test-stdlib.sh z42.time` 16+5+10 = **31/31 全绿**（含 9 个新 ISO-8601 case + 7 已有 DateTime + 5 Stopwatch + 10 TimeSpan）。
- [x] 1.6 归档 + commit + push（hunk-pick 避开 in-flight csprng 改动）。

## 实施期教训

1. **`this.` prefix for instance method calls**：首版调用 `_formatIsoDate()`（无前缀）→ E0401 undefined symbol。z42 当前不做 implicit `this` lookup（不同于 C#），instance method call 必须显式 `this.method()`。改完即过。
2. **测试 fixture 算错差点 commit**：2026-05-26 综合用例最初算了 `1779494400000L`（差 3 天，对应 2026-05-23）；civil_from_days 手动 trace 才发现。规则：**测试期望值至少手算 + 算法 trace 两次再下笔**，不能凭直觉估。手算公式：days = `(year - 1970) * 365 + leap_days_in_range`，month accumulation `Jan=31 / Feb=28(29 leap) / Mar=31 / ...`。

## 备注
- 不引入 timezone 支持（DateTime 本身就是 UTC ms，无 zone 信息）；ISO-8601 中的 `Z` 后缀直接硬编码。timezone-aware 类型留 backlog。
- 算法选 Howard Hinnant `civil_from_days` 因为：(1) 业内事实标准，C++20 chrono 用同一份；(2) 不依赖查表，pure 整数运算；(3) 支持负值（z42 long ms 范围 ±292 年远超需要）。
- `ToString()` override 当前 emit unixMs；保留不动，避免破坏既有 `Equal(0L, dt.UnixMs())` 风格断言。`ToIso8601` 是显式新方法。
