# Tasks: fix YAML test API name (Yaml.Parse → YamlValue.Parse)

> 状态：🟢 已完成 | 创建：2026-05-24 | 完成：2026-05-24 | 类型：fix
> Spec 类型：minimal mode

**变更说明**：z42.yaml 测试错误地调用 `Yaml.Parse(...)` / `Yaml.Stringify(...)`，但实际 API 是 `YamlValue.Parse(...)` / `YamlValue.Stringify(...)`（与 [TomlValue.Parse](../../../src/libraries/z42.toml/src/TomlValue.z42) / [JsonValue.Parse](../../../src/libraries/z42.json/src/JsonValue.z42) 同款 convention）。stdlib 中没有名为 `Yaml` 的类，所以测试编译通过但运行时 dispatch 失败：`undefined function 'Z42YamlParseBasicTests.Yaml.Parse'`。

**根因**：[add-z42-yaml (249a0411, 2026-05-24)](commit) 落地时测试代码写错了 facade 类名。

**Scope**：

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.yaml/tests/parse_basic.z42` | MODIFY | `Yaml.Parse` → `YamlValue.Parse`（16 处）|
| `src/libraries/z42.yaml/tests/parse_strings.z42` | MODIFY | 同上 |
| `src/libraries/z42.yaml/tests/parse_flow.z42` | MODIFY | 同上 |
| `src/libraries/z42.yaml/tests/parse_errors.z42` | MODIFY | 同上 |
| `src/libraries/z42.yaml/tests/parse_block.z42` | MODIFY | 同上 |
| `src/libraries/z42.yaml/tests/stringify.z42` | MODIFY | `Yaml.Stringify` → `YamlValue.Stringify`（8 处）|

**Out of Scope**：
- 不引入 `Yaml` 静态 facade 类（如果想要 BCL 风格的 `Yaml.Parse`/`Yaml.Stringify` 命名一致性，应该 toml/json 同时引入，单独 spec）
- 不动 z42.yaml/src/*（YamlValue.Parse / Stringify API 不变）

## Tasks

- [x] 1.1 `parse_basic.z42` / `parse_strings.z42` / `parse_flow.z42` / `parse_errors.z42` / `parse_block.z42` — `Yaml.Parse` → `YamlValue.Parse`（perl `s/\bYaml\.Parse\b/YamlValue.Parse/g`）
- [x] 1.2 `stringify.z42` — `Yaml.Stringify` → `YamlValue.Stringify`
- [x] 1.3 验证：`./scripts/test-stdlib.sh` z42.yaml 6 个测试文件全过（之前 63 个 not ok → 现在 0）
- [x] 1.4 commit + push（单 commit；含本 spec）
- [x] 1.5 mv → `docs/spec/archive/2026-05-24-fix-yaml-test-api-name/`

## 备注

- 剩余 pre-existing test-stdlib 失败属于不同的 root cause：
  - **z42.io.binary stream tests**（`Z42IoBinaryStreamTests`，约 9 个 not ok）：TypeChecker overload resolution bug —— `new BinaryWriter(memStream)` 应 dispatch 到 `BinaryWriter(Stream)` 但选了 `BinaryWriter(int)`，导致运行时 `MemoryStream Object < 0` 类型不匹配。需要独立 spec `fix-stream-subclass-overload-resolution`。
  - **z42.json/tests/json_stream.z42**：同 TypeChecker overload bug —— `JsonValue.Parse(string)` 与 `JsonValue.Parse(Stream)` 重载，编译期挑错。
  - **z42.io.binary `test_writer_clear_resets_position`**：真实 BinaryWriter.Clear() 行为 bug（expected 0 but got 4），需要独立 spec 修 stdlib 实现。
