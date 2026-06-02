# Tasks: add IPEndPoint wrapper

> 状态：🟢 已完成 | 创建：2026-06-02 | 归档：2026-06-02 | 类型：stdlib feat

## 进度概览
- [x] 阶段 1: `IPEndPoint` class skeleton + constructor validation
- [x] 阶段 2: `ToString` / `Parse` / `Equals`
- [x] 阶段 3: Tests (25/25 green)
- [x] 阶段 4: Doc sync + verify + archive

## 阶段 1: Class + constructor
- [ ] 1.1 Create `src/libraries/z42.net/src/IPEndPoint.z42`
- [ ] 1.2 Fields `_address: IPAddress` + `_port: int`; constructor
      rejects `null` address (ArgumentNullException) + port outside
      [0, 65535] (ArgumentOutOfRangeException — match BCL)
- [ ] 1.3 `Address()` + `Port()` accessors

## 阶段 2: Display + parse + equality
- [ ] 2.1 `override string ToString()` — IPv4 returns `addr:port`;
      IPv6 returns `[addr]:port`
- [ ] 2.2 `static IPEndPoint Parse(string s)` — detect bracketed
      IPv6 (`[...]:port`) vs plain `addr:port` by `s.StartsWith("[")`;
      split on the **last** colon for the plain v4 form so it
      doesn't split a v6 group; throw `FormatException` on missing
      colon / empty address / port out of range
- [ ] 2.3 `bool Equals(IPEndPoint other)` — null-safe; address
      equality via `IPAddress.Equals`; port int compare

## 阶段 3: Tests
- [ ] 3.1 Create `src/libraries/z42.net/tests/ipendpoint.z42`
- [ ] 3.2 `test_construct_with_ipv4` — accessors return expected
- [ ] 3.3 `test_construct_with_ipv6`
- [ ] 3.4 `test_construct_null_address_throws`
- [ ] 3.5 `test_construct_port_below_zero_throws`
- [ ] 3.6 `test_construct_port_above_65535_throws`
- [ ] 3.7 `test_construct_port_edges_0_and_65535_ok`
- [ ] 3.8 `test_tostring_ipv4_addr_port`
- [ ] 3.9 `test_tostring_ipv6_brackets`
- [ ] 3.10 `test_parse_ipv4_addr_port`
- [ ] 3.11 `test_parse_ipv6_bracketed`
- [ ] 3.12 `test_parse_round_trip_ipv4`
- [ ] 3.13 `test_parse_round_trip_ipv6`
- [ ] 3.14 `test_parse_missing_port_throws`
- [ ] 3.15 `test_parse_unbracketed_ipv6_ambiguous_throws`
- [ ] 3.16 `test_parse_port_out_of_range_throws`
- [ ] 3.17 `test_parse_empty_brackets_throws`
- [ ] 3.18 `test_equals_same_address_and_port`
- [ ] 3.19 `test_equals_different_port`
- [ ] 3.20 `test_equals_different_address_family`

## 阶段 4: Docs + verify + archive
- [ ] 4.1 `docs/design/stdlib/net.md`: split
      `net-future-ipaddress` Deferred entry — mark IPEndPoint ✅,
      keep v4mapped / zoneid as separate IDs
- [ ] 4.2 `docs/design/stdlib/roadmap.md`: refine the corresponding
      Deferred Backlog row
- [ ] 4.3 `src/libraries/z42.net/README.md` (if exists): add row
      for `IPEndPoint.z42`
- [ ] 4.4 `./scripts/test-stdlib.sh z42.net` — green
- [ ] 4.5 `./scripts/test-all.sh` — full GREEN
- [ ] 4.6 Move dir to `docs/spec/archive/2026-06-02-add-ipendpoint-wrapper/`
- [ ] 4.7 Commit + push
