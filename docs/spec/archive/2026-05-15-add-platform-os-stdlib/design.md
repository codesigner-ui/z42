# Design: Std.Platform + Std.OperatingSystem

## Architecture

```
┌────────────────────────────────────────────────────────────────┐
│ z42 user code                                                  │
│   if (Platform.IsMacOS()) { ... }                              │
│   int n = OperatingSystem.CpuCount();                          │
└──────────────────────┬─────────────────────────────────────────┘
                       │ 普通 [Native("__name")] dispatch
                       ▼
┌────────────────────────────────────────────────────────────────┐
│ src/libraries/z42.io/src/                                      │
│   Platform.z42         — 3 String getters + 2 KindValue + 8    │
│                          predicates  → 1:1 builtin call        │
│   OperatingSystem.z42  — 7 thin externs                        │
│   Environment.z42      — + UnsetEnv / GetAllVars               │
└──────────────────────┬─────────────────────────────────────────┘
                       │ exec_builtin name → corelib fn
                       ▼
┌────────────────────────────────────────────────────────────────┐
│ src/runtime/src/corelib/                                       │
│   platform.rs  — std::env::consts + os_kind/arch_kind 映射     │
│   system.rs    — std::process::id, std::env::*, libc/winapi    │
│                  for hostname/uname, std::thread::available_parallelism│
│   fs.rs (+=)   — __env_unset / __env_vars                      │
└──────────────────────┬─────────────────────────────────────────┘
                       │ stdlib
                       ▼
┌────────────────────────────────────────────────────────────────┐
│ Rust stdlib + libc + winapi                                    │
│   Unix:     std::env::consts, libc::{gethostname, uname}       │
│   Windows:  std::env::consts, winapi GetComputerNameW /        │
│             RtlGetVersion                                      │
│   Wasm:     std::env::consts ("wasm"/"wasm32"), 其余 graceful  │
│             degrade（uname / hostname 返回 "" 或错误转空串）   │
└────────────────────────────────────────────────────────────────┘
```

**关键决定：z42 facade 只做名字映射，所有跨平台逻辑收敛到 corelib**。

## Decisions

### Decision 1: String + Enum 双形态共存

**问题**：暴露 OS / Arch 一种还是两种形态？

**选项**：
- A. 只 String：`Platform.OS() == "macos"`
- B. 只 Enum：`Platform.OSKind() == OSKind.MacOS`
- C. 两种都暴露：用户挑

**决定**：**C**（user 确认）。

**理由**：
- A 的缺点：拼错字符串编译期发现不了（`"macOs"` → 永远 false）
- B 的缺点：需要 import `OSKind` 常量，简短脚本嫌啰嗦
- C 的成本：API 表面多 4-5 个方法，但实现是同一份枚举映射的两个 view，零额外
  跨平台代码

### Decision 2: 不引入 hostname / os_info crate

**问题**：Hostname / OS version 用 stdlib + libc / winapi 直接写，还是引第三方 crate？

**选项**：
- A. `hostname` + `os_info` crate
- B. libc::gethostname + libc::uname（unix）+ winapi（windows）

**决定**：**B**（user 确认）。

**理由**：
- 维护活跃度：`hostname` crate 近期更新少；`os_info` 引入 ~300 行依赖
- z42 runtime 已经依赖 `libc` 作为间接 dep（其他模块用），加 winapi 是 ~20 行代码
- 跨平台分支 `#[cfg(unix)]` / `#[cfg(windows)]` / `#[cfg(target_arch = "wasm32")]`
  在 corelib 里已经是常规写法（参考 process.rs / fs.rs）
- wasm 上 hostname / uname 不可用 → 返回 `""`（已经是设计上的 graceful degrade）

### Decision 3: OS / Arch 字符串使用 Rust `std::env::consts` 值域

**问题**：`Platform.OS()` 返回 `"darwin"` 还是 `"macos"`？`Arch()` 返回 `"x86_64"` 还
是 `"amd64"`？

**决定**：**用 Rust `std::env::consts::OS` 和 `ARCH` 的原值**：

```text
OS:    "linux" | "macos" | "windows" | "android" | "ios" | "freebsd"
       | "dragonfly" | "netbsd" | "openbsd" | "solaris" | "haiku"
       | "redox" | "fuchsia" | "wasm" | ...
ARCH:  "x86" | "x86_64" | "aarch64" | "arm" | "wasm32" | "powerpc" | ...
```

**理由**：
- 已经是 Rust 程序员熟悉的值，z42 跨语言用户心智成本低
- z42 corelib 直接 pass-through `std::env::consts::OS`，零额外代码
- 不识别的值 z42 facade 端归并到 `"unknown"`（OSKind / ArchKind 都有 `Unknown=0` 兜底）

> macOS 在 Rust 里历史上是 `"macos"`（不是 `"darwin"`），与 .NET `OSPlatform.OSX` 不同；
> z42 选 Rust 命名，与 underlying runtime 一致。

### Decision 4: Kind enum 映射在 corelib 侧固定

**问题**：`OSKind` / `ArchKind` 常量值由 z42 stdlib 定义还是 corelib 定义？

**决定**：**z42 stdlib 定义常量值，corelib 在 `__platform_os_kind` 里**也**硬编码
同样的整数**，两侧用注释互相指向。

**理由**：
- z42 stdlib 暴露 `public static int Linux = 1;` 给用户读
- corelib `__platform_os_kind` 用 match 把 `std::env::consts::OS` → 整数返回
- 双向 sync：z42 改常量值时 corelib 必须改；用 ascii comment（`// keep in sync
  with src/libraries/z42.io/src/Platform.z42 OSKind`）+ z42 单测在每个 OS 上
  验证 `Platform.OSKindValue() == OSKind.<expected>` 把两侧 lock 死

### Decision 5: `Environment.GetEnvironmentVariables()` 返回 `string[]` 而非 Map

**问题**：怎么返回所有 env？

**选项**：
- A. `string[]` of `"KEY=VALUE"`（平铺）
- B. `Map<string, string>`
- C. 两个并行 `string[] Keys()` + `string[] Values()`

**决定**：**A**（user 确认建议）。

**理由**：
- z42 当前 Map<K,V> 的 marshal 路径没稳定（add-std-process 的 generic 泛型字段
  限制同源问题）
- 平铺 `"KEY=VALUE"` 数组直接走 `string[]` extern，零新基础设施
- 调用方一个 for 循环 `kv.Split('=', 2)` 解开，开销可接受
- C 双数组对齐由调用方保障，比 A 更易出错（中间 GC 重新分配丢序）

### Decision 6: `OperatingSystem.OsVersion()` 字符串格式

**问题**：返回什么样的字符串？

**决定**：
- Unix：`uname -srv` 等价，组合为 `"<sysname> <release> <version>"`，例如
  `"Darwin 23.3.0 Darwin Kernel Version 23.3.0: ..."`、`"Linux 6.5.0-23-generic ..."`
- Windows：`<Major>.<Minor>.<Build>`，例如 `"10.0.22631"`
- Wasm：`"wasm"`（固定字面量）
- 拿不到（理论上不会发生）：`""`

**理由**：
- 用户拿这字符串多半是日志 / 上报，无需结构化
- 不解析、不规范化——`uname` 原样输出最忠实
- Windows API 返回数字，用 `.` 拼接也是约定格式

## Implementation Notes

### z42 facade 全貌

#### `Platform.z42`

```z42
namespace Std;

public static class Platform {
    [Native("__platform_os")]
    public static extern string OS();
    [Native("__platform_arch")]
    public static extern string Arch();
    [Native("__platform_family")]
    public static extern string Family();

    [Native("__platform_os_kind")]
    public static extern int OSKindValue();
    [Native("__platform_arch_kind")]
    public static extern int ArchKindValue();

    // 这些 8 个谓词在 z42 侧实现（避免 8 个 builtin trip），各自调一次
    // OSKindValue() 然后比较常量。OSKindValue 走一次 native 跳变，结果
    // 用户层可以缓存到 local。
    public static bool IsLinux()   { return OSKindValue() == OSKind.Linux; }
    public static bool IsMacOS()   { return OSKindValue() == OSKind.MacOS; }
    public static bool IsWindows() { return OSKindValue() == OSKind.Windows; }
    public static bool IsAndroid() { return OSKindValue() == OSKind.Android; }
    public static bool IsIOS()     { return OSKindValue() == OSKind.IOS; }
    public static bool IsWasm()    { return OSKindValue() == OSKind.Wasm; }
    public static bool IsFreeBSD() { return OSKindValue() == OSKind.FreeBSD; }
    public static bool IsUnix() {
        int k = OSKindValue();
        return k == OSKind.Linux || k == OSKind.MacOS || k == OSKind.IOS
            || k == OSKind.Android || k == OSKind.FreeBSD;
    }
}

public static class OSKind {
    public static int Unknown = 0;
    public static int Linux   = 1;
    public static int MacOS   = 2;
    public static int Windows = 3;
    public static int Android = 4;
    public static int IOS     = 5;
    public static int Wasm    = 6;
    public static int FreeBSD = 7;
}

public static class ArchKind {
    public static int Unknown = 0;
    public static int X86_64  = 1;
    public static int Arm64   = 2;   // aarch64
    public static int Wasm32  = 3;
    public static int X86     = 4;
}
```

#### `OperatingSystem.z42`

```z42
namespace Std;

public static class OperatingSystem {
    [Native("__system_pid")]       public static extern int    CurrentPid();
    [Native("__system_exe_path")]  public static extern string ExecutablePath();
    [Native("__system_cwd")]       public static extern string CurrentDirectory();
    [Native("__system_set_cwd")]   public static extern void   SetCurrentDirectory(string path);
    [Native("__system_hostname")]  public static extern string Hostname();
    [Native("__system_cpu_count")] public static extern int    CpuCount();
    [Native("__system_os_version")]public static extern string OsVersion();
}
```

#### `Environment.z42`（扩展）

```z42
// 在现有 class 内追加：
[Native("__env_unset")]
public static extern void UnsetEnvironmentVariable(string name);

[Native("__env_vars")]
public static extern string[] GetEnvironmentVariables();
```

### Rust corelib 实现要点

#### `corelib/platform.rs`

```rust
use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::Result;

pub fn builtin_platform_os(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(std::env::consts::OS.to_string()))
}
pub fn builtin_platform_arch(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(std::env::consts::ARCH.to_string()))
}
pub fn builtin_platform_family(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(std::env::consts::FAMILY.to_string()))
}

// OSKind / ArchKind values — keep in sync with z42.io/src/Platform.z42
pub fn builtin_platform_os_kind(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let kind = match std::env::consts::OS {
        "linux"   => 1,
        "macos"   => 2,
        "windows" => 3,
        "android" => 4,
        "ios"     => 5,
        "wasm"    => 6,   // wasm32-unknown-unknown
        "freebsd" => 7,
        _         => 0,
    };
    Ok(Value::I64(kind))
}
pub fn builtin_platform_arch_kind(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let kind = match std::env::consts::ARCH {
        "x86_64"  => 1,
        "aarch64" => 2,
        "wasm32"  => 3,
        "x86"     => 4,
        _         => 0,
    };
    Ok(Value::I64(kind))
}
```

#### `corelib/system.rs`

```rust
pub fn builtin_system_pid(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::I64(std::process::id() as i64))
}

pub fn builtin_system_exe_path(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    match std::env::current_exe() {
        Ok(p)  => Ok(Value::Str(p.to_string_lossy().into_owned())),
        Err(_) => Ok(Value::Str(String::new())),
    }
}

pub fn builtin_system_cwd(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    match std::env::current_dir() {
        Ok(p)  => Ok(Value::Str(p.to_string_lossy().into_owned())),
        Err(_) => Ok(Value::Str(String::new())),
    }
}

pub fn builtin_system_set_cwd(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = require_str(args, 0, "__system_set_cwd")?;
    std::env::set_current_dir(&path)?;
    Ok(Value::Null)
}

pub fn builtin_system_hostname(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(get_hostname().unwrap_or_default()))
}

#[cfg(unix)]
fn get_hostname() -> Option<String> {
    use std::ffi::CStr;
    let mut buf = [0u8; 256];
    let rc = unsafe {
        libc::gethostname(buf.as_mut_ptr() as *mut _, buf.len())
    };
    if rc == 0 {
        let cstr = unsafe { CStr::from_ptr(buf.as_ptr() as *const _) };
        Some(cstr.to_string_lossy().into_owned())
    } else { None }
}

#[cfg(windows)]
fn get_hostname() -> Option<String> {
    use winapi::um::winbase::GetComputerNameW;
    let mut size = 256u32;
    let mut buf = vec![0u16; size as usize];
    let ok = unsafe { GetComputerNameW(buf.as_mut_ptr(), &mut size) };
    if ok != 0 {
        Some(String::from_utf16_lossy(&buf[..size as usize]))
    } else { None }
}

#[cfg(not(any(unix, windows)))]
fn get_hostname() -> Option<String> { None }   // wasm

pub fn builtin_system_cpu_count(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let n = std::thread::available_parallelism()
        .map(|n| n.get() as i64).unwrap_or(1);
    Ok(Value::I64(n))
}

pub fn builtin_system_os_version(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    Ok(Value::Str(get_os_version()))
}

#[cfg(unix)]
fn get_os_version() -> String {
    let mut utsname: libc::utsname = unsafe { std::mem::zeroed() };
    if unsafe { libc::uname(&mut utsname) } != 0 {
        return String::new();
    }
    fn cstr_from_arr(arr: &[libc::c_char]) -> String {
        let cstr = unsafe { std::ffi::CStr::from_ptr(arr.as_ptr()) };
        cstr.to_string_lossy().into_owned()
    }
    format!("{} {} {}",
        cstr_from_arr(&utsname.sysname),
        cstr_from_arr(&utsname.release),
        cstr_from_arr(&utsname.version))
}

#[cfg(windows)]
fn get_os_version() -> String {
    // RtlGetVersion via ntdll;详见 implementation phase
    String::new()  // placeholder; real impl in tasks
}

#[cfg(not(any(unix, windows)))]
fn get_os_version() -> String { String::from("wasm") }
```

#### `corelib/fs.rs` 增加

```rust
pub fn builtin_env_unset(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let key = require_str(args, 0, "__env_unset")?;
    std::env::remove_var(&key);
    Ok(Value::Null)
}

pub fn builtin_env_vars(ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let list: Vec<Value> = std::env::vars()
        .map(|(k, v)| Value::Str(format!("{k}={v}")))
        .collect();
    Ok(ctx.heap().alloc_array(list))
}
```

### 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| `libc::gethostname` 在某些 musl 静态链接环境返回空 | hostname 拿不到 | fallback：读 `HOSTNAME` env var；wasm 走 `""` |
| Windows `winapi` 依赖目前 z42 runtime 是否已经有 | 需要加 dep | 仅在 `#[cfg(windows)]` 路径用；`winapi` ~10 KB cargo dep，z42 已有间接 |
| OSKind / ArchKind 整数值漂移 | 双侧不同步 → silent 错误 | z42 单测在 `Platform.IsXxx()` 链路覆盖每个 OS；CI cross-OS 跑可发现 |
| Wasm target 上 `current_exe()` 返回错误 | ExecutablePath 空串 | 已设计成错误归并到 `""`；z42 文档明示 |
| `set_current_dir` 影响整个进程 | 改一次全局生效 | 文档 + warning；脚本用例可接受 |

### z42 编译器 bug：同文件多个含 static-field 的 static class（implementation note）

实施期发现：在 `Platform.z42` 中先后声明 `OSKind`（static fields 8 项）和
`ArchKind`（static fields 5 项），第二个类的 static field 在运行时无法访问
（`Assert.True(ArchKind.Unknown == 0)` 失败，且任何对 `ArchKind.*` 的读取均触发
silent "VM error"）。

**根因（推测）**：z42 编译器在处理同一文件内的连续 `public static class { ... }`
时，第二个 class 的 static field initializer 没被正确串到 module init 路径，
导致字段读取时槽位为 `null`。**OSKind 单独 / ArchKind 单独都正常**，只是
"两个同文件" 触发问题。

**workaround（本 spec 采用）**：把 `ArchKind` 拆到独立文件
`src/libraries/z42.core/src/ArchKind.z42`。同 namespace 不变，对调用方完全无感知。
拆完后 z42.core 文件数 49 → 52（+Platform, +OperatingSystem, +ArchKind）。

**遗留 follow-up**：z42 编译器层面 fix（让多类同文件 static initializer 正确编译）
不属于本 spec scope。已记入 docs/design/compiler/compiler-architecture.md 的
known limitations。

## Testing Strategy

- **Rust 单测**（`platform_tests.rs` / `system_tests.rs`）：
  - `__platform_os` 返回与 `std::env::consts::OS` 一致
  - `__platform_os_kind` 返回 OSKind 期望值（CI 各平台 cross-check）
  - `__system_pid > 0`
  - `__system_cwd` 与 Rust 直接 `current_dir()` 字符串等长 / 等内容
  - `__system_set_cwd` → 切换后 `__system_cwd` 反映
  - `__env_unset` → 后续 `__env_get` 返回 null
  - `__env_vars` 数组形态校验（含某个已知存在的 key）

- **z42 集成测试**（`tests/platform.z42` / `tests/environment_extra.z42`）：
  - `Platform.IsLinux() || Platform.IsMacOS()` 至少一个 true（CI 主机覆盖）
  - `Platform.OSKindValue() != OSKind.Unknown`
  - `OperatingSystem.CpuCount() >= 1`
  - `OperatingSystem.Hostname()` 非空字符串（在主 CI 上）
  - `Environment.UnsetEnvironmentVariable` → `GetEnvironmentVariable` 返回 null

- **跨平台**：linux-x64 + macos-arm64 CI runner 都跑；wasm CI build 但 skip 这套测试

## Estimated Effort

约 1 天：

- z42 facade（Platform.z42 + OperatingSystem.z42 + Environment.z42 扩展）：0.2 天
- Rust corelib（platform.rs + system.rs + fs.rs 扩展）：0.3 天
- 单测 + 集成测试：0.2 天
- 文档 + 跨平台调试（Windows uname / hostname 边界）：0.2 天
- spec 归档 + IncrementalBuildIntegrationTests 计数同步：0.1 天
