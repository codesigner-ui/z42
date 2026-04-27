# Tasks: add-z42-test-v0

> 状态：🟢 已完成 | 类型：feat (new stdlib package) | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** 新增 `z42.test` 包 v0：极简 imperative TestRunner，支持 stdlib 自身和用户脚本写单元测试。

**为什么 v0**：z42 当前缺：
- **Lambda / 函数引用** → 不能写 `runner.Run("name", () => Assert.Equal(...))`
- **通用 Attribute** → 不能写 `[Test] public void TestX() { ... }`
- **Reflection** → 不能自动扫描方法

所以 v0 用最朴素的 try/catch 模式：

```z42
using Std.Test;

void Main() {
    var t = new TestRunner("MyTests");

    t.Begin("Addition");
    try { Assert.Equal(4, 2 + 2); } catch (Exception e) { t.Fail(e); }

    t.Begin("Concatenation");
    try { Assert.Equal("ab", "a" + "b"); } catch (Exception e) { t.Fail(e); }

    return t.Summary();  // exit code = failed count
}
```

**v1 规划**（待 lambda 支持）：
```z42
runner.Run("Addition", () => Assert.Equal(4, 2 + 2));  // 自动 try/catch
```

**v2 规划**（待 attribute + reflection）：
```z42
[Test] public void TestAddition() { Assert.Equal(4, 2 + 2); }
// runner 自动发现并运行 [Test] 方法
```

## Tasks

- [x] 1.1 新建 `src/libraries/z42.test/z42.test.z42.toml`：lib 包，依赖 z42.core
- [x] 1.2 新建 `src/libraries/z42.test/src/TestRunner.z42`：核心 TestRunner 类
- [x] 1.3 更新 `src/libraries/z42.workspace.toml`：把 z42.test 加入 default-members
- [x] 1.4 更新 `scripts/build-stdlib.sh::LIBS`：加 `z42.test`
- [x] 2.1 新建 `src/libraries/z42.test/README.md`：用法 + 三阶段路线图
- [x] 3.1 新增 golden test `src/runtime/tests/golden/run/18_test_runner/`：演示并锁定 TestRunner 行为（含 pass + fail 两种场景）
- [x] 4.1 更新 `src/libraries/README.md` 库列表 + 未来计划（z42.test 状态从"L2/L3 用户脚本测试需求"挪到"已启动"）
- [x] 5.1 build-stdlib + regen + dotnet test + test-vm 全绿
- [x] 6.1 commit + push + 归档

## 备注

- 不依赖任何 native 库，纯脚本（与 z42.text / z42.collections 同性质）
- 不引入新 builtin
- TestRunner.Summary() 返回 failed 计数（int），调用者用作 `return t.Summary();` → process exit code
