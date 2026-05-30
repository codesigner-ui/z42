# Spec: HttpClient auto-decompress brotli

## ADDED Requirements

### Requirement: SetAutoDecompress(true) 发 `Accept-Encoding: gzip, br`

#### Scenario: 请求 header 带双算法

- **WHEN** `c.SetAutoDecompress(true)`，调用 `c.Send(request)`
- **THEN** 请求 wire 上的 `Accept-Encoding` header 为 `gzip, br`（顺序固定，
  无 quality value）

#### Scenario: 显式 Accept-Encoding 不被覆盖

- **WHEN** `c.SetAutoDecompress(true)`，request.Headers.Set("Accept-Encoding", "identity")
- **THEN** 请求 wire 上的 `Accept-Encoding` 保持 `identity`（已有逻辑：
  `!request.Headers.Contains("Accept-Encoding")` 才 set 默认值）

### Requirement: `Content-Encoding: br` 响应自动解码

#### Scenario: brotli body 透明解码

- **WHEN** server 返回 `Content-Encoding: br` + brotli-encoded body
- **THEN** `HttpResponse.Body` 已是解码后的原始 bytes；`Content-Encoding`
  header 保留（caller 可 inspect）

#### Scenario: 解码失败抛 HttpException（设计层）

- **WHEN** server 返回 `Content-Encoding: br` + malformed body
- **THEN** 抛 `Std.Net.Http.HttpException`，message 包含 `auto-decompress: brotli decode failed`
- **NOTE**: 单测被延后 — `brotli` Rust crate 的 decoder 对任意 garbage
  bytes 可死循环而非快速返回错误，单测会挂死。验证路径在代码 review
  层（catch 包装），运行时 negative coverage 等 brotli decoder timeout
  或 curated-corrupt-payload fixture（follow-up `compression-future-brotli-decode-error-coverage`）

#### Scenario: 大小写不敏感

- **WHEN** server 返回 `Content-Encoding: BR`（uppercase / mixed）
- **THEN** decode 仍触发（已有逻辑：`.ToLower()` 后比较）

## MODIFIED Requirements

### Requirement: Accept-Encoding default 从 `gzip` → `gzip, br`

**Before:** `request.Headers.Set("Accept-Encoding", "gzip");`

**After:** `request.Headers.Set("Accept-Encoding", "gzip, br");`

### Requirement: response body decode 增加 brotli 分支

**Before:**

```z42
if (enc == "gzip" && resp.Body != null && resp.Body.Length > 0) {
    try { resp.Body = Gzip.Decompress(resp.Body); }
    catch (Exception e) { throw new HttpException("auto-decompress: gzip decode failed: " + e.Message); }
}
```

**After:**

```z42
if (resp.Body != null && resp.Body.Length > 0) {
    if (enc == "gzip") {
        try { resp.Body = Gzip.Decompress(resp.Body); }
        catch (Exception e) { throw new HttpException("auto-decompress: gzip decode failed: " + e.Message); }
    } else if (enc == "br") {
        try { resp.Body = Brotli.Decompress(resp.Body); }
        catch (Exception e) { throw new HttpException("auto-decompress: brotli decode failed: " + e.Message); }
    }
}
```

## IR Mapping

无 — 纯 stdlib 改动。

## Pipeline Steps

- [ ] Lexer — N/A
- [ ] Parser / AST — N/A
- [ ] TypeChecker — N/A
- [ ] IR Codegen — N/A
- [ ] VM interp — N/A
