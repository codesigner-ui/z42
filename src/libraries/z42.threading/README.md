# z42.threading — 线程库

## 职责

OS 线程级并发原语 — 用户层 `Thread.Start(Action) / Join()` API。底层通过
`__thread_spawn` / `__thread_join` builtin 接入 runtime 的 `VmCore.threads`
slot table。

## src/ 核心文件

| 文件 | 类型 | 说明 |
|------|------|------|
| `Thread.z42` | `Std.Threading.Thread` | OS 线程句柄；`Start(Action)` 工厂 + `Join()` 同步等待 |
| `ThreadException.z42` | `Std.ThreadException` | 跨线程异常封装（worker `throw` 或 Rust panic 经由 Join 透传） |

## 入口点

```z42
using Std.Threading;

var t = Thread.Start(() => {
    Console.WriteLine("hello from worker");
});
t.Join();
```

## 依赖关系

仅依赖 `z42.core`（异常基类 + delegate `Action`）。
