# Tasks: Rewrite z42-test-runner with Compile-Time Discovery

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 R1 + R2 完成。

## 进度概览

- [ ] 阶段 1: crate 骨架
- [ ] 阶段 2: discover.rs + result.rs
- [ ] 阶段 3: runner.rs (执行 + sink + Setup/Teardown)
- [ ] 阶段 4: bencher.rs (warmup + 统计)
- [ ] 阶段 5: format/{tap,json,pretty}
- [ ] 阶段 6: scripts/test-changed.sh
- [ ] 阶段 7: justfile 接入
- [ ] 阶段 8: 文档
- [ ] 阶段 9: 验证

---

## 阶段 1: Crate 骨架

- [ ] 1.1 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) workspace.members 加 `../toolchain/test-runner`
- [ ] 1.2 [src/toolchain/test-runner/Cargo.toml](src/toolchain/test-runner/Cargo.toml) 依赖 z42-runtime + clap + serde + serde_json + anyhow + regex + colored
- [ ] 1.3 [src/toolchain/test-runner/src/main.rs](src/toolchain/test-runner/src/main.rs) clap 入口
- [ ] 1.4 [src/toolchain/test-runner/README.md](src/toolchain/test-runner/README.md)
- [ ] 1.5 [src/toolchain/README.md](src/toolchain/README.md) 列入 test-runner

## 阶段 2: discover + result

- [ ] 2.1 [src/toolchain/test-runner/src/result.rs](src/toolchain/test-runner/src/result.rs) TestStatus / TestResult / TestSuiteResult
- [ ] 2.2 [src/toolchain/test-runner/src/discover.rs](src/toolchain/test-runner/src/discover.rs) discover() 函数（按 design.md Decision 2）
- [ ] 2.3 collect_zbc_files：递归扫描目录
- [ ] 2.4 应用 --filter regex / --tag 过滤

## 阶段 3: Runner

- [ ] 3.1 [src/toolchain/test-runner/src/runner.rs](src/toolchain/test-runner/src/runner.rs) run_one() 函数
- [ ] 3.2 sink 安装 + 取出（调用 R2 的 native）
- [ ] 3.3 Setup → 主体 → Teardown 顺序调度
- [ ] 3.4 异常分类（TestFailure / SkipSignal / 其他 / ShouldThrow 期望）
- [ ] 3.5 TestCase 参数化展开

## 阶段 4: Bencher

- [ ] 4.1 [src/toolchain/test-runner/src/bencher.rs](src/toolchain/test-runner/src/bencher.rs) run_benchmark() 函数
- [ ] 4.2 closure 提取协议（thread-local stash + take）
- [ ] 4.3 warmup + samples + median + IQR 计算
- [ ] 4.4 与 P1 baseline-schema.json 兼容（输出可被 bench-diff.sh 消费）

## 阶段 5: Formatter

- [ ] 5.1 [src/toolchain/test-runner/src/format/mod.rs](src/toolchain/test-runner/src/format/mod.rs) Formatter trait
- [ ] 5.2 [src/toolchain/test-runner/src/format/tap.rs](src/toolchain/test-runner/src/format/tap.rs) TAP 13
- [ ] 5.3 [src/toolchain/test-runner/src/format/json.rs](src/toolchain/test-runner/src/format/json.rs) JSON
- [ ] 5.4 [src/toolchain/test-runner/src/format/pretty.rs](src/toolchain/test-runner/src/format/pretty.rs) TTY 友好（colored）
- [ ] 5.5 默认 format：TTY 自动 pretty，否则 tap

## 阶段 6: scripts/test-changed.sh

- [ ] 6.1 [scripts/test-changed.sh](scripts/test-changed.sh) 按原 P2 spec design.md Decision 8
- [ ] 6.2 反向依赖关系硬编码（6 个 stdlib 库）
- [ ] 6.3 用 jq 输出 JSON

## 阶段 7: justfile

- [ ] 7.1 [justfile](justfile) `test-changed` 替换为完整实现
- [ ] 7.2 `test-stdlib` 替换为完整实现（含 lib 参数）
- [ ] 7.3 `test-integration` 替换为完整实现

## 阶段 8: 文档

- [ ] 8.1 [docs/design/test-runner.md](docs/design/test-runner.md) 完整设计
- [ ] 8.2 [docs/design/testing.md](docs/design/testing.md) 加 R3 段（runner 在大图中位置）
- [ ] 8.3 [src/toolchain/test-runner/README.md](src/toolchain/test-runner/README.md) 用法 + 示例
- [ ] 8.4 [docs/dev.md](docs/dev.md) 加 "z42-test-runner" 段

## 阶段 9: 验证

- [ ] 9.1 `cargo build -p z42-test-runner --release` 通过
- [ ] 9.2 [src/toolchain/test-runner/tests/integration_test.rs](src/toolchain/test-runner/tests/integration_test.rs) 单测全绿
- [ ] 9.3 手动：写一个 demo .z42（含 [Test] 通过/失败/skip/should_throw 各 1）→ 编译 → runner 跑 → 输出符合 spec
- [ ] 9.4 --format tap / --format json / --format pretty 各自正确
- [ ] 9.5 --filter 过滤生效
- [ ] 9.6 --bench 模式跑通至少 1 个 [Benchmark]
- [ ] 9.7 退出码：通过=0、失败=1、错误=2、无测试=3
- [ ] 9.8 现有 just test 全绿（不影响 vm_core / cross-zpkg）
- [ ] 9.9 just test-stdlib z42.test 跑通 z42.test 自身（dogfooding 占位 OK）

## 备注

### 实施依赖

- R1 (test metadata section) 必须先落地
- R2 (z42.test library) 必须先落地（runner 调用 R2 的 native）
- 与 R4 / R5 无依赖

### 风险

- **风险 1**：Interpreter 公开 API 不足以从 host 调任意函数 + 注入参数 → 实施前调研 src/runtime/src/interp/，可能需要先 R3.0 (extend-interpreter-host-api)
- **风险 2**：Closure value 提取 + 调用协议未定 → 阶段 4 实施时确定
- **风险 3**：异常类型识别用字符串 contains —— 不可靠；R4 加 type_idx 后改 typed
- **风险 4**：CI 上 runner 跑慢（每个 [Test] 一个独立 Interpreter） → 串行 v0.1 接受；v0.2 加并行

### 工作量估计

3-4 天：
- crate + 接口骨架：0.5 天
- discover + result：0.5 天
- runner 主流程（sink + Setup-Teardown + 异常分类）：1 天
- Bencher：0.5 天
- Formatter：0.5 天
- scripts/test-changed.sh + justfile + 文档：0.5 天
- 集成验证 + 调试：0.5 天
