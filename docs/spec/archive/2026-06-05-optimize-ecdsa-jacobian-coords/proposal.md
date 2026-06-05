# Proposal: Jacobian-coordinate scalar multiplication for short-Weierstrass ECDSA

## Why

ECDSA over secp256k1 / P-256 currently uses **affine** point arithmetic: every
`_pointDouble` / `_pointAdd` computes a 256-bit modular inverse (`ModInverse`) to
divide by the denominator. A scalar multiplication is ~256 doublings + ~128 adds,
so a single scalar mult performs ~384 modular inversions, and a full sign+verify
round trip (~4 scalar mults) performs **~1500 inversions**. On the interpreted VM
each inversion is an extended-Euclid loop over 256-bit `BigInt`s — hundreds of
big-number ops. The result: a round trip takes 60–180 s on shared CI runners.

This already broke CI: `test_secp256k1_sign_verify_round_trip` exceeded its 240 s
`[Timeout]` under `test-all.sh --parallel --jobs=4` contention on a 4-vCPU
ubuntu-x64 runner and was killed (`recalibrate-secp256k1-timeout`, 2026-06-01,
commit 581a28af raised the cap to 600 s as a stopgap). The cap hides the slowness;
it does not fix it.

**Standard fix:** Jacobian projective coordinates `(X, Y, Z)` where `x = X/Z²`,
`y = Y/Z³`. Point doubling and addition then need **no** inversion — only field
mul/sqr/add. A single inversion converts back to affine **once** at the end of the
scalar mult. This drops ~384 inversions/scalar-mult to **1**, an ~100× reduction
in the dominant cost.

## What Changes

- Replace affine `_pointDouble` / `_pointAdd` / `_scalarMult` in the two
  short-Weierstrass curves (secp256k1, P-256) with Jacobian formulas.
- Add a single affine←Jacobian conversion (one `ModInverse`) at scalar-mult exit.
- Hoist curve constants (`p`, `n`) so they are parsed once, not re-`ParseHex`'d per
  point op (a secondary inefficiency observed alongside the inversions).
- **Observable output must be byte-identical** — same public keys, same RFC 6979
  deterministic signatures, same verify results. This is a pure performance
  refactor; all existing test vectors must pass unchanged.
- Once fast, restore `test_secp256k1_sign_verify_round_trip`'s `[Timeout]` to a
  tight value (demonstrating the override) instead of the 600 s stopgap.

## Scope (允许改动的文件)

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.crypto/src/EcdsaSecp256k1.z42` | MODIFY | Jacobian point arithmetic + constant hoist |
| `src/libraries/z42.crypto/src/EcdsaP256.z42` | MODIFY | same optimization (shares affine pattern) |
| `src/libraries/z42.crypto/tests/ecdsa_secp256k1_vectors.z42` | MODIFY | tighten `[Timeout]` back once fast; add Jacobian-edge-case vectors |
| `src/libraries/z42.crypto/tests/ecdsa_p256_vectors.z42` | MODIFY | add edge-case vectors (identity/Z=0 paths) if present |
| `docs/design/<crypto-ec>.md` | MODIFY | document Jacobian rationale + affine-output invariant |

**只读引用：**

- `src/libraries/z42.numerics/` (BigInt) — understand `Mod`/`ModInverse`/`Multiply` cost
- `src/libraries/z42.crypto/src/X25519.z42`, `Ed25519.z42` — confirm they use
  different coordinate systems (Montgomery ladder / twisted Edwards); out of scope here

## Out of Scope

- X25519 (Montgomery ladder) and Ed25519 (twisted Edwards) — different curve forms
  with their own coordinate strategies; separate effort if they show the same cost.
- Constant-time / side-channel hardening — z42 crypto is not yet making
  constant-time guarantees; do not regress, but do not expand the claim here.
- Endomorphism (GLV) / wNAF / windowed methods — further speedups layered on top of
  Jacobian; track separately if still too slow after this change.

## Open Questions

- [ ] Tighten the secp256k1 round-trip `[Timeout]` to what value once fast? (Measure
      first; pick with margin but tight enough to still demo the override.)
- [ ] Does P-256 have an analogous slow test currently passing only by luck of a
      looser cap? (Check before declaring done.)
