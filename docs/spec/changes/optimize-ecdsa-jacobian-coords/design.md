# Design: Jacobian-coordinate scalar multiplication for short-Weierstrass ECDSA

## Architecture

```
GeneratePublicKey / Sign / Verify  (public API — unchanged signatures + output)
        │
        ▼
_scalarMult(k, P_affine)
        │  convert P → Jacobian (X=x, Y=y, Z=1)
        ▼
double-and-add loop over k bits
        │  _jacDouble / _jacAdd   ← NO ModInverse, only field mul/sqr/add
        ▼
_jacToAffine(R)   ← exactly ONE ModInverse (Z⁻¹), then x=X·Z⁻², y=Y·Z⁻³
        │
        ▼
affine (x, y)  → bytes  (identical to today)
```

Curve constants `p` (field prime) and `n` (group order) are read once at the top of
each public entry point and threaded down as parameters, replacing the current
`_curvePrime()` / `_curveOrder()` calls that re-`BigInt.ParseHex` a 64-char string
on every field op.

## Decisions

### Decision 1: Jacobian vs. standard projective vs. Montgomery ladder
**问题:** Which inversion-free coordinate system for short-Weierstrass `y²=x³+ax+b`?
**选项:**
- A — **Jacobian** `(X,Y,Z)`, `x=X/Z²`, `y=Y/Z³`. Best-studied doubling/add formulas
  for Weierstrass; `a=0` (secp256k1) and `a=-3` (P-256) both have well-known fast
  specializations.
- B — Standard projective `(X,Y,Z)`, `x=X/Z`, `y=Y/Z`. Slightly more muls than
  Jacobian for these curves.
- C — Montgomery ladder. Great for X25519, but secp256k1/P-256 are Weierstrass; a
  ladder needs x-only formulas + y-recovery, more complex here.
**决定:** A (Jacobian). Standard, matches both curves' `a` specializations, and the
double-and-add structure stays close to the current code (lowest-risk port).

### Decision 2: Preserve affine output exactly (byte-for-byte invariant)
**问题:** How do we guarantee no observable change?
**决定:** Convert back to affine at scalar-mult exit, so all bytes-out paths
(`_pointToBytes`, `_modN(R[0], n)` in sign/verify) see the same affine coordinates as
today. The Jacobian representation is purely internal. Existing reference vectors
(`scalar_1_yields_generator`, `scalar_2_yields_2g`, Bitcoin/Ethereum round trips,
RFC 6979 determinism) are the conformance gate — they must pass unchanged. This is
the core safety property; if any vector shifts, the port is wrong.

### Decision 3: Identity / point-at-infinity handling
**问题:** Affine code uses `null` for the identity and special-cases `P==Q` →
double, `x_P==x_Q && y_P==-y_Q` → infinity. Jacobian needs `Z=0` for infinity.
**决定:** Represent infinity as `Z=0` internally; map to/from the existing `null`
affine convention at the conversion boundaries so callers are unaffected. Add
explicit edge-case tests: `k·G` producing identity, doubling a point with `y=0`,
adding `P + (−P)`.

### Decision 4: Keep secp256k1 and P-256 as separate files (no premature shared module)
**问题:** Both curves get the same optimization — extract a shared EC core?
**决定:** Keep separate for this change. The constants and `a`-specializations differ
(`a=0` vs `a=-3`), the files already mirror each other, and a shared generic EC
module is a larger refactor with its own risk. Port each in place; revisit a shared
core only if a third Weierstrass curve appears. (Per philosophy: don't grow an
abstraction speculatively.)

## Implementation Notes

- Jacobian doubling (a=0, secp256k1): `S=4XY²; M=3X²; X'=M²−2S; Y'=M(S−X')−8Y⁴; Z'=2YZ`.
- Jacobian doubling (a=−3, P-256): use the `M=3(X−Z²)(X+Z²)` trick (exploits `a=−3`).
- Jacobian add: standard `(X1,Y1,Z1)+(X2,Y2,Z2)`; detect doubling (`U1==U2 && S1==S2`)
  and infinity (`U1==U2 && S1!=S2`).
- All intermediate arithmetic stays `mod p`; reduce with the existing `_modP` but
  pass `p` in rather than re-parsing.
- The single final `ModInverse(Z, p)` is the only inversion per scalar mult.

## Testing Strategy

- **Conformance (must pass byte-identical):** all existing secp256k1 + P-256 vectors
  — generator/2G points, Bitcoin/Ethereum round trips, RFC 6979 determinism,
  tamper-rejection, off-curve rejection, length validation.
- **New edge cases:** identity via `Z=0`, doubling at `y=0`, `P+(−P)`, scalar `n−1`.
- **Performance gate:** measure round-trip wall time before/after locally; expect a
  large drop (target: well under 30 s locally, comfortably under a tightened CI cap).
- **GREEN:** full `./scripts/test-all.sh --scope=full`; confirm ubuntu-x64
  `test_secp256k1_sign_verify_round_trip` no longer approaches its cap.
