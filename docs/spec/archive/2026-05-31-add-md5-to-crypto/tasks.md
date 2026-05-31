# Tasks: Md5 + HmacMd5

> 状态：🟢 已完成 | 创建：2026-05-31 | 归档：2026-05-31 | 类型：feat（新 stdlib 类）

## 进度

- [x] 1.1 NEW `src/libraries/z42.crypto/src/Md5.z42` — RFC 1321 实现
      （Hash / HashString / HashHex / HashStringHex）
- [x] 1.2 MODIFY `src/libraries/z42.crypto/src/Hmac.z42` — 加 `HmacMd5` 类
- [x] 1.3 NEW `src/libraries/z42.crypto/tests/md5_vectors.z42` — RFC 1321 §A.5
      6 试向量 + NIST CAVP `MD5LongMsg`
- [x] 1.4 NEW `src/libraries/z42.crypto/tests/hmac_md5_vectors.z42` — RFC 2202 §2
      7 试向量
- [x] 1.5 MODIFY `docs/design/stdlib/crypto.md` — API 表 + ⚠️ legacy 注释
- [x] 1.6 GREEN: `./scripts/build-stdlib.sh` + `./scripts/test-stdlib.sh z42.crypto`
      + `./scripts/test-all.sh`
- [x] 1.7 归档 → `docs/spec/archive/2026-05-31-add-md5-to-crypto/`
- [x] 1.8 commit + push

## 备注

- 实现结构 mirror Sha1：long-mask-32 防 int32 溢出；4-register state 而非
  Sha1 的 5；little-endian byte/word order（vs Sha1 big-endian）
- RFC 1321 §3.4 给的 K[0..63] 常量 = `floor(abs(sin(i+1)) * 2^32)`
  全部 32-bit unsigned；用 long 字面量存
- Per-round 函数：F / G / H / I 按 RFC 1321 §3.4 定义；shift amounts S[0..63]
  也按 RFC table
- HmacMd5 完全按 RFC 2104：key > 64 → key = Md5(key); ipad/opad 0x36/0x5c

## 实施备注（2026-05-31）

- 14 MD5 + 8 HMAC-MD5 tests GREEN, including RFC 1321 §A.5 全 7 vectors,
  block-boundary edges, NIST CAVP million-'a', UTF-8 multibyte, + RFC 2202
  §2 全 7 vectors HMAC-MD5
- 第一轮 UTF-8 test 我自己 hand-guessed 错 hash；shell `md5(1)` 校对后改为
  正确值 `07117fe4a1ebd544965dc19573183da2`（"café" UTF-8）。教训：MD5
  vector 不要 hand-guess，always cross-check shell。
