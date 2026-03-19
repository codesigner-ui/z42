# Hot Reload（热更新）

## 背景与目标

热更新允许在 VM 运行期间**替换函数实现**，无需重启进程。主要面向**游戏脚本**场景（AI 行为、关卡逻辑、UI 脚本），支持开发阶段快速迭代和生产环境热修复。

---

## 触发方式

### 方式 1：`[HotReload]` 注解（推荐）

在命名空间或函数上声明，VM 自动监听对应 `.zmod` 文件变化：

```z42
[HotReload]
namespace Game.Scripts;

void OnUpdate(float dt) { ... }
void OnCollision(Entity a, Entity b) { ... }
```

### 方式 2：显式 API

在运行时代码中主动触发重载：

```z42
HotReload.Reload("Game.Scripts");        // 立即重载指定模块
HotReload.Watch("Game.Scripts");         // 开启文件监听（等价于 [HotReload] 注解）
HotReload.Unwatch("Game.Scripts");       // 停止监听
```

---

## 语义

### 替换粒度：函数级

- 热更新以**函数**为最小替换单位，按函数名匹配。
- 重载完成后，**下一次**调用该函数时使用新实现。
- 正在执行中的调用帧**不受影响**，继续执行旧代码直至该帧返回。

### 状态保留规则

| 状态 | 热更新后 |
|------|---------|
| 模块级全局变量 | **保留**（不重置） |
| 函数实现（字节码） | **替换** |
| 当前正在执行的栈帧 | **不受影响**（继续执行旧代码） |
| 闭包 / lambda | **替换**（下次调用时） |
| 类型定义（class/struct） | **不支持热更新**（见限制） |

### 回调通知

模块可声明热更新前后的钩子：

```z42
[HotReload]
namespace Game.Scripts;

// 热更新前调用（可用于保存临时状态）
static void OnBeforeReload() { ... }

// 热更新后调用（可用于恢复状态）
static void OnAfterReload() { ... }
```

---

## Phase 1 限制

| 限制项 | 说明 |
|--------|------|
| 仅支持 `ExecMode.Interp` | JIT / AOT 模式调用 `HotReload.*` 会抛运行时错误 |
| 不允许修改函数签名 | 参数数量、类型、返回类型必须与原版本一致；签名不匹配时重载失败并报错 |
| 不允许新增 / 删除函数 | 只能替换已有函数体；新增函数需重启或等待 Phase 2 |
| 不支持类型定义热更新 | class / struct / enum 定义变更需重启 |
| 文件监听依赖 `.z42ir.json` | Phase 1 热更新读取 IR JSON，而非源码（编译仍需手动触发） |

---

## Feature Gate

`z42.toml` 中显式开启，默认关闭：

```toml
[features]
hot_reload = true   # 默认 false，建议仅 dev profile 开启
```

命令行覆盖：

```bash
z42vm --feature hot_reload <file.z42ir.json>
```

---

## IR / VM 实现要点（供后续实现参考）

- **无需新 IR 指令**。VM 层直接替换 `Module.functions` 中对应的 `Function` 对象。
- `Module` 需改为 `Arc<RwLock<Module>>` 以支持并发读写。
- 文件监听推荐使用 [`notify`](https://crates.io/crates/notify) crate（跨平台，支持 debounce）。
- 重载流程：
  1. 检测到 `.z42ir.json` 变化
  2. 反序列化新模块
  3. 签名校验（参数数/类型一致性）
  4. 调用 `OnBeforeReload`（如存在）
  5. 写锁替换 `Function` 对象
  6. 调用 `OnAfterReload`（如存在）
  7. 记录日志：`[hot-reload] reloaded N functions in Game.Scripts`

---

## 与 ExecMode 的关系

```
ExecMode.Interp  →  支持热更新 ✅
ExecMode.Jit     →  不支持（Phase 1），Phase 2 考虑函数级 JIT re-compile
ExecMode.Aot     →  不支持（编译期固化，无运行时替换路径）
```

既然 `[ExecMode(Mode.Interp)]` 已用于标注"快速启动、热重载"场景，`[HotReload]` 注解与 `Interp` 模式天然配套。
