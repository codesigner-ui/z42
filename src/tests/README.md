# src/tests/ — 中央 VM 端到端测试集

## 职责

按特性分类的 z42 VM 端到端测试集，对标 [dotnet/runtime/src/tests/](https://github.com/dotnet/runtime/tree/main/src/tests)。

支持两种用例形态（dual-mode discovery，2026-05-08）：
- **Dir 模式** — `<category>/<name>/` 目录含 `source.z42` + 可选 sidecar 文件（适合需要 `features.toml` / `expected_output.txt` / `expected_error.txt` / `expected.zasm` 等 sidecar 的用例）
- **Flat 模式** — `<category>/<name>.z42` 单文件（仅 assert-only run 用例：用 `Std.Assert` 抛异常表达失败，期望空 stdout，无任何 sidecar）

不放在这里：
- 编译器单元测试 → [src/compiler/z42.Tests/](../compiler/z42.Tests/)
- VM Rust 单元测试 → [src/runtime/src/](../runtime/src/) 同模块的 `*_tests.rs`
- VM Rust 集成测试（zbc_compat / native interop / manifest schema）→ [src/runtime/tests/](../runtime/tests/)
- stdlib 库本地测试 → [src/libraries/<lib>/tests/](../libraries/)

## 类别

| 类别 | 内容 |
|------|------|
| `basic/` | 基础功能：hello / fibonacci / arrays / namespace / assert dogfood |
| `exceptions/` | try/catch/finally / 嵌套 / stack trace / exception subclass |
| `generics/` | 泛型函数 / 类 / 约束 / 实例化 / interface dispatch |
| `inheritance/` | virtual / abstract / multilevel / implicit object base |
| `interfaces/` | multi-interface / 属性 / IComparer / event |
| `delegates/` | delegate / multicast / event / nested |
| `closures/` | lambda / closure / local function |
| `gc/` | GC cycle / collect / weak ref / weak subscription |
| `types/` | enum / struct / record / typeof / is/as / nullable / numeric aliases / char |
| `control_flow/` | switch / do-while / null-coalesce / null-conditional / loop control / nested |
| `operators/` | bitwise / 增量 / parse / postfix / 逻辑 / 比较 / 重载 |
| `refs/` | ref / out / in / nested ref |
| `classes/` | class / namespace / access / static / auto-property / ctor / indexer |
| `strings/` | string builtin / 方法 / 静态方法 / 边界 / script-mode |
| `parse/` | 仅 ZASM-match 测试（无 .zbc / 无 stdout 比对，由 xUnit ParseTests 跑）|
| `errors/` | 编译失败用例（`expected_error.txt` 期望诊断）|
| `cross-zpkg/` | 多 zpkg 端到端（target / ext / main 三方协作；由 `test-cross-zpkg.sh` 跑） |

## 用例文件约定

### Dir 模式（`<category>/<name>/`）

| 文件 | 何时存在 | 含义 |
|------|---------|------|
| `source.z42` | 必须 | z42 源码 |
| `source.zbc` | run / parse | 由 `regen-golden-tests.sh` 生成；不入库（gitignored） |
| `source.zasm` | 可选 | ZASM 调试文本 |
| `expected_output.txt` | run | stdout 期望（**空 = 删除**；缺失 = assert-only 模式：用例靠 `Std.Assert` 抛异常表达失败，期望空 stdout）|
| `expected_error.txt` | error | 编译诊断期望 |
| `expected.zasm` | parse | IR ZASM 期望 |
| `features.toml` | 可选 | LanguageFeatures override |
| `interp_only` | 可选 marker | 跳过 JIT 模式 |

### Flat 模式（`<category>/<name>.z42`）

仅适用于 assert-only run 用例。无任何 sidecar — 期望空 stdout，使用 `LanguageFeatures.Phase1` 默认配置。
对应的 `<name>.zbc` 由 `regen-golden-tests.sh` 生成（不入库）。

**何时使用 Flat 模式**：用例只调用 `Assert.*`（无 `Console.WriteLine` 输出对照），且不需要 features 覆盖、emit 格式覆盖、interp_only 标记或任何其他 sidecar。

## 添加新测试

按以下顺序判断归属（先到先得）：

1. **库 API 行为** → `src/libraries/<lib>/tests/<name>[.z42]`
2. **编译失败** → `src/tests/errors/<name>/`（必须 dir，含 `expected_error.txt`）
3. **仅 ZASM 匹配** → `src/tests/parse/<name>/`（必须 dir，含 `expected.zasm`）
4. **跨多 zpkg** → `src/tests/cross-zpkg/<name>/`
5. **其他 VM/编译器特性**：
   - 用 `Console.WriteLine` 测打印行为 / 需要 sidecar → `src/tests/<category>/<name>/source.z42` + sidecars（dir 模式）
   - 仅用 `Assert.*` 测计算 / 控制流，无 sidecar → `src/tests/<category>/<name>.z42`（flat 模式）
   - 不确定类别归 `basic/`

完整规则见 [docs/design/testing.md](../../docs/design/testing.md)。

## 运行

```bash
just test-vm                # 全部 run + parse 用例（interp + jit）
just test-cross-zpkg        # 仅 cross-zpkg
dotnet test src/compiler/z42.Tests/z42.Tests.csproj  # xUnit 跑全部三类
```
