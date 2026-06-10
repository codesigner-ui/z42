# Tasks: fix-stale-build-stdlib-sh-refs

> 状态：🟢 已完成 2026-06-11

**变更说明：** runtime/compiler 的多处用户可见报错 + 注释引用了已不存在的 `./scripts/build-stdlib.sh`（migrate-scripts-to-z42 把其逻辑折进 `xtask_stdlib.z42 _buildStdlibCore`，经 `z42 xtask.zpkg build stdlib` 调用）。把陈旧引用全部改为 `z42 xtask.zpkg build stdlib`（注释里用简短 `xtask build stdlib`）。
**原因：** 用户实测撞到报错 `zpkg minor N not supported (writer is at M); regen via ./scripts/build-stdlib.sh` —— 指向的脚本已删，误导。
**类型：** fix（纯字符串/注释清理，零行为变更，无需测试）。
**文档影响：** 无（改的就是 message/comment 文本）。
**子系统：** runtime（zbc_reader.rs / main.rs）+ compiler（ZpkgReader.cs / SingleFileCompiler.cs / 2 测试）+ toolchain（hello_c/main.c 注释一行，与 port-z42c-core 非重叠）。锁均空闲/非重叠。

- [x] 1.1 `src/runtime/src/metadata/zbc_reader.rs`（2 处 bail! 报错 + 1 注释）
- [x] 1.2 `src/runtime/src/main.rs`（println 提示 + 2 注释）
- [x] 1.3 `src/compiler/z42.Project/ZpkgReader.cs`（报错 + 注释）
- [x] 1.4 `src/compiler/z42.Pipeline/SingleFileCompiler.cs`（注释）
- [x] 1.5 `src/compiler/z42.Tests/StdlibDelegateTests.cs` + `MulticastActionTests.cs`（异常消息）
- [x] 1.6 `src/toolchain/host/examples/hello_c/main.c`（注释）
- [x] 1.7 验证：cargo build (release) + dotnet build slnx 全绿（纯字符串变更，零行为影响）

## 备注
- 保留的 `build-stdlib` 字面量是合法的、非脚本引用：`ArgParser("build-stdlib", ...)`（z42.cli 示例程序名）、"build-stdlib logic folded into xtask"（CI/xtask 的准确历史描述）、`scripts/build-stdlib.z42`（former .z42，非 .sh）。
- `docs/spec/changes/migrate-scripts-to-z42/tasks.md` 里的 build-stdlib.sh 提及是该迁移 change 自身的历史描述，不动。
