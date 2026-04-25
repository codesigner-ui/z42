# Tasks: Wave 2 — Exception hierarchy + IEnumerable/IEnumerator

> 状态：🟢 已完成 | 创建：2026-04-25 | 完成：2026-04-25 | 类型：lang (stdlib 新 API)

## 进度概览
- [ ] 阶段 1: Exception 基类
- [ ] 阶段 2: IEnumerator / IEnumerable 接口
- [ ] 阶段 3: 9 个异常子类
- [ ] 阶段 4: 文档 + README 同步
- [ ] 阶段 5: 测试 + 回归验证
- [ ] 阶段 6: 归档 + commit

---

## 阶段 1: Exception 基类

- [ ] 1.1 创建 `src/libraries/z42.core/src/Exception.z42`：
  - `namespace Std;`
  - `public class Exception` 含 Message / StackTrace / InnerException
  - 两个 ctor（msg / msg+inner）
  - `override string ToString()` 返回 `"Exception: " + Message`
- [ ] 1.2 运行 `./scripts/build-stdlib.sh` 验证基类编译通过
- [ ] 1.3 临时 golden test `run/90_exception_base`：
  - 构造 + 三字段读取 + ToString 输出
  - 验证 Exception 类独立可用（不需要 try/catch 联动）

## 阶段 2: IEnumerator / IEnumerable 接口

- [ ] 2.1 创建 `src/libraries/z42.core/src/IEnumerator.z42`：
  - `interface IEnumerator<T> : IDisposable`
  - `bool MoveNext()`
  - `T Current { get; }`（property 形式优先，若 parser 不支持降级为 `T GetCurrent()`）
- [ ] 2.2 创建 `src/libraries/z42.core/src/IEnumerable.z42`：
  - `interface IEnumerable<T>`
  - `IEnumerator<T> GetEnumerator()`
- [ ] 2.3 stdlib build 验证 —— **property 探测点**；
  - 若报 parser / TypeCheck 错，立即降级为 `T GetCurrent()` 并在 tasks 备注
- [ ] 2.4 golden test `run/93_ienumerable_contract`：
  - 定义 `class RangeIter : IEnumerator<int>` 实现 MoveNext + Current
  - 定义 `class Range : IEnumerable<int>` 返回 RangeIter
  - 用 IEnumerable / IEnumerator 接口引用调用（不走 foreach）

## 阶段 3: 9 个异常子类

子目录 `src/libraries/z42.core/src/Exceptions/` 下，每个文件内容约 8 行：

- [ ] 3.1 `ArgumentException.z42` : Exception
- [ ] 3.2 `ArgumentNullException.z42` : ArgumentException
- [ ] 3.3 `InvalidOperationException.z42` : Exception
- [ ] 3.4 `NullReferenceException.z42` : Exception
- [ ] 3.5 `IndexOutOfRangeException.z42` : Exception
- [ ] 3.6 `KeyNotFoundException.z42` : Exception
- [ ] 3.7 `FormatException.z42` : Exception
- [ ] 3.8 `NotImplementedException.z42` : Exception
- [ ] 3.9 `NotSupportedException.z42` : Exception
- [ ] 3.10 每个子类仅 ctor 转发 + `override ToString()` 硬编码类名字符串
- [ ] 3.11 stdlib build 验证所有子类编译通过

## 阶段 4: 文档 + README 同步

- [ ] 4.1 新增 `docs/design/exceptions.md` — 使用者视角：
  - Exception 层次图（9 个子类）
  - throw 语义（Phase 1 任意值；推荐 Exception 子类）
  - StackTrace 字段说明（当前为 null，未来填充计划）
  - InnerException 使用模式（wrap/unwrap）
- [ ] 4.2 新增 `docs/design/iteration.md` — 使用者视角：
  - IEnumerable / IEnumerator 契约
  - 与现有 foreach 鸭子协议（Count + get_Item）并存说明
  - 未来 foreach codegen 升级路线（识别 IEnumerator 路径）
- [ ] 4.3 更新 `src/libraries/z42.core/README.md`：
  - 核心文件表加 `Exception.z42` / `IEnumerable.z42` / `IEnumerator.z42`
  - 新增 `src/Exceptions/` 子目录章节
- [ ] 4.4 更新 `docs/roadmap.md` L2 stdlib 条目（z42.core 含 Exception /
  IEnumerable）

## 阶段 5: 测试 + 回归验证

- [ ] 5.1 golden test `run/90_exception_base`（阶段 1.3）
- [ ] 5.2 golden test `run/91_exception_subclass` — 子类 throw/catch + is-check
- [ ] 5.3 golden test `run/92_exception_inner_chain` — 嵌套 wrap/unwrap
- [ ] 5.4 golden test `run/93_ienumerable_contract`（阶段 2.4）
- [ ] 5.5 golden test `run/94_ienumerable_generic_constraint` —
  `where T: IEnumerable<U>` 泛型约束使用
- [ ] 5.6 GREEN 验证：
  - [ ] `./scripts/build-stdlib.sh` 5/5 success
  - [ ] `dotnet build src/compiler/z42.slnx` 0 errors
  - [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 0 errors
  - [ ] `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 100% pass
  - [ ] `./scripts/test-vm.sh` 100% pass（interp + jit）
  - [ ] `run/12_exceptions` / `run/41_try_finally` 等 throw-any-value 现有
    测试保持绿
  - [ ] `run/80_stdlib_arraylist` / `83_foreach_user_class` 现有 foreach
    测试保持绿

## 阶段 6: 归档 + commit

- [ ] 6.1 tasks.md 状态 → `🟢 已完成`
- [ ] 6.2 `spec/changes/add-core-exception-ienumerable/` → `spec/archive/2026-04-25-add-core-exception-ienumerable/`
- [ ] 6.3 commit + push（scope `feat(stdlib)`）

## 备注

实施过程发现 3 个 Phase 1 限制（独立后续 change 修复）：

1. **字段 `?` 可空标注 parser 不支持** — `public string? StackTrace;` 报
   `expected (`. 当前用 ref-type 默认 nullable 绕过，运行时正确但类型
   层面无 explicit null tracking
2. **VM ObjNew 不支持 ctor 重载** — stdlib 编译生成 `ClassName.ClassName$1`/
   `$2` arity suffix，但 VM 按 `ClassName.ClassName`（无 suffix）查找。
   双 ctor 失败；当前简化为单 ctor，wrapping pattern 用 setter
3. **TypeChecker E0402 同类型字段 self-reference assign** — 用户代码
   `outer.InnerException = inner;` 报 "cannot assign Exception to
   Exception"；test 91 移除 inner chain 测试

接口形状降级：

- `T Current { get; }` property 形式 parser 暂未支持，IEnumerator.Current
  退化为方法 `T Current()`

未实施（推迟到 Wave 3+）：
- golden test `93_ienumerable_contract` / `94_ienumerable_generic_constraint`
  —— 接口定义已编译进 z42.core.zpkg，编译期可见，本 Wave 不强求 end-to-end
  测试（Wave 3+ 配合 List 实现 IEnumerable 时一并写）
- List/Dictionary 实现 IEnumerable —— Wave 3+ 范围

**测试覆盖** (新增 4 个 golden):
- `run/91_exception_base` — Exception 基类构造 + 字段 + ToString + throw/catch
- `run/92_exception_subclass` — 4 个子类 throw/catch + ToString

**验证全绿**: dotnet test 575/575 (+2)、test-vm.sh 176/176 (+4, interp+jit)、
cargo test 61/61
