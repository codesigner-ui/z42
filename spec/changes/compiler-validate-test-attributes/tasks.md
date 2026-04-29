# Tasks: Compiler-Validate Test Attributes

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 R1 + R2 完成。

## 进度概览

- [ ] 阶段 1: TestAttributeValidator 类
- [ ] 阶段 2: 5 类校验规则 (Z0911-Z0915)
- [ ] 阶段 3: Attribute 组合校验
- [ ] 阶段 4: TestCase 参数数量校验
- [ ] 阶段 5: PipelineCore 集成
- [ ] 阶段 6: Z0911-Z0915 错误码注册
- [ ] 阶段 7: 单元测试
- [ ] 阶段 8: 文档同步
- [ ] 阶段 9: 验证

---

## 阶段 1: Validator 类骨架

- [ ] 1.1 [src/compiler/z42.Semantics/TestAttributeValidator.cs](src/compiler/z42.Semantics/TestAttributeValidator.cs) 新建
- [ ] 1.2 构造函数收 (DiagnosticBag, SemanticModel)
- [ ] 1.3 `Validate(CompilationUnit)` 入口

## 阶段 2: 5 类签名校验

- [ ] 2.1 ValidateTestSignature → Z0911
- [ ] 2.2 ValidateBenchmarkSignature → Z0912
- [ ] 2.3 ValidateShouldThrow → Z0913 + 写入 expected_throw_type_idx
- [ ] 2.4 ValidateSkip → Z0914
- [ ] 2.5 ValidateSetupTeardown → Z0915

## 阶段 3: 组合校验

- [ ] 3.1 [Test] vs [Benchmark] 互斥 → Z0911
- [ ] 3.2 [Setup]/[Teardown] vs [Test]/[Benchmark] 互斥 → Z0915
- [ ] 3.3 [Skip]/[Ignore] 必须搭 [Test]/[Benchmark] → Z0914
- [ ] 3.4 [ShouldThrow] 必须搭 [Test] → Z0913

## 阶段 4: TestCase 参数校验

- [ ] 4.1 每个 [TestCase] args.Count == fn.Parameters.Count
- [ ] 4.2 不匹配 → Z0911 (针对该 [TestCase] span)

## 阶段 5: PipelineCore 集成

- [ ] 5.1 [src/compiler/z42.Pipeline/PipelineCore.cs](src/compiler/z42.Pipeline/PipelineCore.cs) TypeCheck 后插入 TestAttributeValidator pass
- [ ] 5.2 错误时不进入 IrGen（同其他 TypeCheck 错误处理）

## 阶段 6: 错误码注册

- [ ] 6.1 [src/compiler/z42.Core/Diagnostics/ErrorCodes.cs](src/compiler/z42.Core/Diagnostics/ErrorCodes.cs) 注册 Z0911-Z0915 message 模板
- [ ] 6.2 [docs/design/error-codes.md](docs/design/error-codes.md) 替换 R1 占位为完整描述（触发条件 + 修复建议 + 示例）

## 阶段 7: 单元测试

- [ ] 7.1 [src/compiler/z42.Tests/TestAttributeValidatorTests.cs](src/compiler/z42.Tests/TestAttributeValidatorTests.cs) 新建
- [ ] 7.2 每错误码 ≥ 1 positive + 1 negative case（design.md 测试矩阵 ~17 用例）
- [ ] 7.3 组合校验 4 个 case
- [ ] 7.4 TestCase 数量校验 3 个 case

## 阶段 8: 文档同步

- [ ] 8.1 [docs/design/testing.md](docs/design/testing.md) 加 R4 段（attribute 校验规则）
- [ ] 8.2 [docs/roadmap.md](docs/roadmap.md) 更新

## 阶段 9: 验证

- [ ] 9.1 `dotnet build src/compiler/z42.slnx` 通过
- [ ] 9.2 `dotnet test src/compiler/z42.Tests/` 全绿（含新 ValidatorTests）
- [ ] 9.3 之前合法测试代码（z42.test 库本身）继续编译通过
- [ ] 9.4 故意写错的 examples/test_demo.z42 变体确实报对应 Z091X
- [ ] 9.5 错误信息含 file:line + Span underline

## 备注

### 实施依赖

- R1 (TestEntry 数据结构) 必须先落地
- R2 (z42.test.Bencher / Exception 类型) 必须先落地（验证用）
- 不依赖 R3 / R5

### 与其他 sub-spec 的关系

- **R3 受益**：runner 假设输入合法，简化运行时分类逻辑
- **R5 受益**：重写 golden 时第一时间发现签名错误

### 风险

- **风险 1**：SemanticModel 的 LookupType / IsSubtypeOf API 不完全 → 实施时调研，视情况扩展
- **风险 2**：attribute 系统对参数类型推断不完全 → 与 R2 调研结果联动
- **风险 3**：错误码顺序冲突（与现有诊断码） → 用 Z0911-0915 范围已在 R1 占位

### 工作量估计

1.5-2 天：
- Validator 类 + 5 类规则：1 天
- 组合校验 + TestCase：0.5 天
- 单元测试 + 错误码注册：0.5 天
