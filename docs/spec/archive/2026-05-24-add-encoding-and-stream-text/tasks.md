# Tasks: add Encoding + StreamReader / StreamWriter

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24
> 类型：feat（new stdlib classes, no VM changes）

- [x] 1.1 NEW `src/libraries/z42.encoding/src/Encoding.z42` — concrete class wrapping Utf8 statics
- [x] 1.2 NEW `src/libraries/z42.encoding/tests/encoding.z42` — round-trip + offset/count tests
- [x] 2.1 MODIFY `src/libraries/z42.io/z42.io.z42.toml` — add z42.encoding dep
- [x] 2.2 NEW `src/libraries/z42.io/src/StreamReader.z42` — drain-and-decode v0
- [x] 2.3 NEW `src/libraries/z42.io/src/StreamWriter.z42` — encode-on-write
- [x] 3.1 NEW `src/libraries/z42.io/tests/stream_reader.z42`
- [x] 3.2 NEW `src/libraries/z42.io/tests/stream_writer.z42`
- [x] 3.3 NEW `src/libraries/z42.io/tests/stream_text_pipeline.z42`
- [x] 4.1 MODIFY `docs/design/stdlib/io-stream.md` — flip Deferred to ✅ landed
- [x] 4.2 MODIFY `docs/design/stdlib/roadmap.md` — Stream 索引行
- [x] 4.3 MODIFY `docs/design/stdlib/encoding.md` — Encoding class section
- [x] 4.4 MODIFY `src/libraries/z42.io/README.md` — list new files
- [x] 5.1 `./scripts/test-stdlib.sh` 全绿（增加 ~30 test 全 pass）
- [x] 5.2 Archive + commit + push
