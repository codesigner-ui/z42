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
| `Mutex.z42` | `Std.Threading.Mutex<T>` | 排他互斥；RAII callback `Lock(Func<T,T>)`（add-sync-primitives 2026-05-20） |
| `Channel.z42` | `Std.Threading.Channel<T>` | unbounded MPSC 管道：`Send` / `Recv` / `TryRecv` / `Close`（add-sync-primitives 2026-05-20） |
| `ChannelDisconnectedException.z42` | `Std.ChannelDisconnectedException` | 所有 sender 关闭且队列空时 `Recv()` 抛出 |

## 入口点

```z42
using Std.Threading;

// Spawn / Join
var t = Thread.Start(() => {
    Console.WriteLine("hello from worker");
});
t.Join();

// Mutex — RAII callback；锁内 (long v) => v + 1 读改写自动 unlock
var counter = new Mutex<long>(0);
counter.Lock((long v) => v + 1);

// Channel — 跨线程 FIFO 队列
var c = new Channel<long>();
Thread.Start(() => { c.Send(42); c.Close(); });
long v = c.Recv();
```

## 依赖关系

仅依赖 `z42.core`（异常基类 + delegate `Action` / `Func`）。
