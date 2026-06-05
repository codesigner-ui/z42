# Proposal: 0.3.x 三主线规划（stdlib 整理 + 自举启动 + 反射 MVP）

> 状态：📋 规划已审批（2026-06-05）｜类型：roadmap 重排｜责任人：User + Claude

## 背景

0.2.0 发布前期工作已收尾。User 决定将 0.3.x 重排为三条并行主线：

1. **标准库整理 + 性能**（"模块划分和性能都不是很好"）
2. **编译器自举**（"可以并行，等全部实现了在进行替换"）
3. **反射 MVP**（从原 0.5.1–0.5.3 提前到 0.3.x）

原 0.3.x 五项（Golden 全 L1 覆盖 / 调试符号 / 热重载 / GC v1 / Profiler）全部推 0.4.x 起，唯有 **GC v1 提前到 0.3.0** 作为三主线共同前置。

## User 已裁决

| 决策点 | 裁决 |
|------|------|
| B 主线 0.3.x 范围 | 仅 Lexer + Project + Driver + Parser 四个易做子系统 |
| C 主线 0.3.x 深度 | 只读元数据 + typeof / GetType + Attribute reflection |
| GC v1 时机 | **前置到 0.3.0**（A/B 共同前置） |
| 原 0.3.x 项处理 | 全推 0.4.x 起 |

## 三主线概览

### A — 标准库整理 + 性能

**目标**：22 个包的模块划分梳理（合并 / 拆分 / 重命名）+ 命名空间一致性 + 公开 API 边界 + 数据驱动的 perf 攻坚（不靠拍脑袋）

**子版本**：A0 spec → A1 重组 → A2 bench baseline → A3/A4/A5 三轮 perf 攻坚

**已识别 perf 候选**（需 A2 bench 数据确认排序）：

- BigInt：Karatsuba（[numerics.md Deferred](../../../design/stdlib/numerics.md)）
- List / Dict 热路径（[collections.md](../../../design/stdlib/collections.md)）
- String / StringBuilder
- Path / Encoding
- JSON / YAML / TOML reader（共用 lexer 抽取）

**已识别组织争议**（A0 spec 需裁决）：

- Console placement：z42.io vs z42.core prelude（[organization.md](../../../design/stdlib/organization.md) 草案中三个候选）
- 命名空间一致性：`Std.IO.binary` vs `Std.IoBinary`
- 22 包中是否有可合并 / 可拆分项

### B — 编译器自举（**0.3.x 不完成**）

**0.3.x 目标**：完成无 L3 依赖的 4 个易做子系统 + 建立**逐子系统 bit-identical 验证 CI gate**

**B 主线四子系统**：

| 子系统 | 现有 C# 对应 | L3 依赖 | 难点 |
|------|-------------|:----:|------|
| Lexer | z42.Syntax/Lexer/* | 无 | 字符迭代；现有 z42.text Char API 足够 |
| Project manifest reader | z42.Project/ProjectManifest.cs | 无 | z42.toml 已有 TOML reader |
| Driver CLI | z42.Driver/Program.cs | 无 | z42.cli arg parsing 已有 |
| Parser | z42.Syntax/Parser/* | 部分（用虚方法替代 visitor） | AST 节点改 class；下推 visitor pattern；代码量大 |

**剩余子系统推迟到 0.5.x**：

- Semantic：visitor pattern 强依赖 lambda（L3-C）
- TypeChecker：AST 集合强依赖 generic（L3-G）
- IR builder / lowering：同上
- ZbcWriter / ZpkgWriter：依赖 generic 元数据
- Pipeline：依赖上述

**CI gate**：每子系统 z42 实现产物（zpkg / 字符串输出）与 C# 实现产物**逐字节对账**。任一字节差异 → CI 红。

**0.3.x 退出**：4 个易做子系统 CI bit-identical 对账 7 日内零飘移。

### C — 反射 MVP

**0.3.x 范围**：

- C0 Spec：`Type` / `MethodInfo` / `FieldInfo` / `PropertyInfo` / `ParameterInfo` API 形状；与 zpkg `TypeDesc` / `MethodDesc` 元数据的映射
- C1：runtime 暴露 `Type.GetMembers / GetMethods / GetFields / GetProperties` 系列
- C2：`typeof(T)` 编译器关键字 + `obj.GetType()` runtime intrinsic + z42.reflection 包公开
- C3：Attribute reflection（前置：**用户自定义 attribute 机制 spec 需先落地**）

**0.3.x 不做**（推 0.5.x L3-R 完整版）：

- `Method.Invoke` / `Activator.CreateInstance<T>()` / `Type.MakeGenericType` — 强依赖 generic instantiation

## 0.3.x 子版本编排

```
0.3.0  GC v1（A/B/C 共同前置）

0.3.1  A0 spec + B0 spec + C0 spec（三主线 spec 同步起草）

0.3.2  A1 包结构重组（重组目录 + namespace + 调用点全量更新）

0.3.3  A2 bench baseline ║ B1 Lexer in z42 ║ C1 metadata 暴露

0.3.4  A3 perf #1 BigInt/Coll ║ B2 Project manifest ║ C2 typeof + GetType

0.3.5  A4 perf #2 String/IO ║ B3 Driver CLI ║ C3 Attribute reflection
       （前置：attribute 机制 spec 在 0.3.4 已起草）

0.3.6  A5 perf #3 JSON/YAML/TOML ║ B4 Parser in z42

0.3.7  收尾：B CI bit-identical gate 全绿 + A perf delta report
```

`║ =` 同子版本三主线并行推进。

## 依赖图

```
0.3.0 GC v1 ──► 0.3.1 三 spec ──► 0.3.2 A 包重组
                                       │
                                       ▼
                              0.3.3 A bench ║ B Lexer ║ C metadata
                                       │
                                       ▼ (A 重组完成后 B/C 才有稳定的包路径引用)
                              0.3.4–0.3.6 三主线并行
                                       │
                                       ▼
                              0.3.7 收尾 + gate

0.3.5 C3 Attribute ◄── attribute 机制 spec（0.3.4 起草）
                      ◄── C2 typeof（C 链内依赖）

0.5.x L3-G 泛型 + L3-C lambda 落地 ──► 0.5 B 剩余子系统
                                  ──► 0.5 反射完整版（Method.Invoke）
                                  ──► 1.0 byte-identical 替换
```

## 退出标准（GREEN）

- **A 主线**：22 包审计 spec 落地 + 重组完成 + 每包 bench baseline + 三轮 perf 攻坚 delta report 公开（bench-baselines branch）
- **B 主线**：Lexer / Project / Driver / Parser 4 子系统 z42 实现 CI bit-identical gate 7 日零飘移
- **C 主线**：z42.reflection 包公开 + 4 个反射对象类型 + GetMembers 系列 + typeof / GetType + Attribute reflection；MVP 单元测试覆盖率 ≥ 90%

## Open Questions（spec 起草阶段需 User 裁决）

1. **A0**：22 包审计中合并 / 拆分提案（package-by-package 裁决）
2. **A0**：Console placement 最终方案（A/B/C 三选一）
3. **B0**：bit-identical CI gate 触发频率（每 PR / 每日 / 每 commit）
4. **C0**：`Type` 对象的生命周期（cached / per-call new）+ 与 GC 的交互
5. **C3 前置**：attribute 机制 spec 范围（仅 [Test] / 通用用户定义 / 何种语法）

---

**审批**：User 2026-06-05 已批四项裁决；本 proposal 与 [roadmap.md §0.3.x](../../../roadmap.md) 同步登记。具体 A0 / B0 / C0 三 spec 在 0.3.1 启动时分别开 `add-stdlib-reorg-audit` / `add-bootstrap-easy-subsystems` / `add-reflection-mvp` 三个独立 change spec。
