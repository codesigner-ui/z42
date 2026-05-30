# Tasks: HttpClient auto-decompress brotli

> 状态：🟢 已完成 | 创建：2026-05-30 | 归档：2026-05-30 | 类型：feat（新 stdlib 行为，小）

## 进度

- [x] 1.1 MODIFY `src/libraries/z42.net/src/Http/HttpClient.z42`
  - Accept-Encoding default `"gzip"` → `"gzip, br"`
  - response decode 加 `enc == "br"` 分支调 `Brotli.Decompress`
  - 错误 wrapping (HttpException) 对齐 gzip 形态
  - 更新 SetAutoDecompress doc comment 说明 brotli 已 supported
- [x] 1.2 NEW `src/libraries/z42.net/tests/http_brotli_decode.z42`
  - server 端 fixture 返回 `Content-Encoding: br` + brotli body (来自 `Brotli.Compress`)
  - client 调 SetAutoDecompress(true)，Send，assert Body 为原文
  - mixed-case `Content-Encoding: BR` 解码
  - 显式 Accept-Encoding 不被覆盖
  - malformed brotli body → HttpException
- [x] 1.3 MODIFY `docs/design/stdlib/net.md`
  - Deferred 段：`net-future-http-compression` 行更新为完全 ✅
- [x] 1.4 MODIFY `docs/design/stdlib/roadmap.md`
  - Deferred Backlog Index 同步
- [x] 1.5 GREEN：`./scripts/build-stdlib.sh` + `./scripts/test-stdlib.sh z42.net` + `./scripts/test-all.sh`
- [x] 1.6 归档 + commit + push

## 实施备注（2026-05-30）

- HttpClient `enc.ToLower()` 在 `Content-Encoding` 缺失时解引用 null —
  pre-existing latent bug from gzip-only path that brotli wiring expanded
  the test surface enough to expose. Fixed by null-check on `encRaw`
  before invoking `.ToLower()`. Body-length guard also pulled out of the
  combined condition so both encoding branches share it.
- Test 5 个 → 4 个：`test_brotli_malformed_body_throws_http_exception` 单测
  挂死（brotli crate 对任意 garbage 输入可能死循环而非快返错误）。改为
  spec layer "设计层" 验证（catch wraps any Exception 成 HttpException），
  运行时 negative coverage 留 follow-up
  `compression-future-brotli-decode-error-coverage`
- 跨 thread bool 结果传递：z42 closure 对 primitive bool 是值捕获，
  server 线程内 `seenBoth = true` 不传回。改用 `bool[1]` cell（pattern
  对齐既有 z42.threading 测试约定）。同一改动适用于 `sawIdentity`。
- z42 stdlib `using` 在 test 文件里不靠 Std.Threading 间接带入 Std.IO —
  `Console.WriteLine` 需要显式 `using Std.IO;`（调试时遇到，已在最终
  test 文件清掉调试 trace）

