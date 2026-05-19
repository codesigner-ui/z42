# Spec: Array default initialization

## MODIFIED Requirements

### Requirement: `new T[N]` zero-initializes elements

**Before:** `new T[N]` allocates `N` slots all filled with `Value::Null`,
regardless of `T`. Reading any slot before a write produces `Null`. Casts
like `(int)arr[i]` on the unwritten slot crash with bare `VM error: cast Null to int`.

**After:** `new T[N]` allocates `N` slots filled with the **default value for T**:

| `T` (declared element type)                                                                                                | Default `Value`     |
| -------------------------------------------------------------------------------------------------------------------------- | ------------------- |
| `int` / `long` / `short` / `byte` / `sbyte` / `ushort` / `uint` / `ulong` / `i8` / `i16` / `i32` / `i64` / `u8` / `u16` / `u32` / `u64` | `Value::I64(0)`     |
| `double` / `float` / `f32` / `f64`                                                                                         | `Value::F64(0.0)`   |
| `bool`                                                                                                                     | `Value::Bool(false)`|
| `char`                                                                                                                     | `Value::Char('\0')` |
| `string` / class / interface / unknown                                                                                     | `Value::Null`       |

The behavior matches `default_value_for` already in use by `ObjNew` field init.

#### Scenario: byte array zero-fills

- **WHEN** script executes `byte[] arr = new byte[8]; int v = (int)arr[0];`
- **THEN** `v` evaluates to `0` (no crash); all 8 slots are `Value::I64(0)`

#### Scenario: int array zero-fills

- **WHEN** script executes `int[] arr = new int[4]; int v = arr[3];`
- **THEN** `v` evaluates to `0`

#### Scenario: long array zero-fills

- **WHEN** script executes `long[] arr = new long[2]; long v = arr[0] + arr[1];`
- **THEN** `v` evaluates to `0L`

#### Scenario: bool array false-fills

- **WHEN** script executes `bool[] arr = new bool[4]; bool v = arr[0];`
- **THEN** `v` evaluates to `false`

#### Scenario: double array zero-fills

- **WHEN** script executes `double[] arr = new double[3]; double v = arr[1];`
- **THEN** `v` evaluates to `0.0`

#### Scenario: char array null-char fills

- **WHEN** script executes `char[] arr = new char[2]; char v = arr[0];`
- **THEN** `v` evaluates to `'\0'`

#### Scenario: string array null-fills (reference type)

- **WHEN** script executes `string[] arr = new string[2];`
- **THEN** `arr[0]` and `arr[1]` evaluate to `null` (unchanged from prior behavior)

#### Scenario: user-class array null-fills

- **WHEN** script executes `MyClass[] arr = new MyClass[3];`
- **THEN** all slots are `null` (unchanged from prior behavior — reference type stays null)

#### Scenario: zero-length array allocates empty

- **WHEN** script executes `int[] arr = new int[0];`
- **THEN** `arr.Length` evaluates to `0`; no element access required

#### Scenario: array literal initialization unchanged

- **WHEN** script executes `int[] arr = new int[]{1, 2, 3};` (ArrayNewLit, separate opcode)
- **THEN** elements are exactly `1, 2, 3` (no default-value involvement; ArrayNewLit path untouched)

### Requirement: zbc minor version bump

#### Scenario: writer bumps zbc minor

- **WHEN** the compiler writes any zbc file after this change is in effect
- **THEN** `ZbcWriter.VersionMinor` is incremented by exactly 1 from its pre-change value;
  the zbc header reports the new minor; `docs/design/runtime/zbc.md` Minor changelog
  has an entry for this minor

#### Scenario: reader rejects mismatched minor (strict pin)

- **WHEN** the VM loads any zbc file whose `(major, minor)` differs from
  `(ZBC_VERSION_MAJOR, ZBC_VERSION_MINOR)`
- **THEN** loading fails with the existing version-mismatch error; no fallback
  to older minor (unchanged strict-pin policy)

#### Scenario: zpkg minor bumps in lockstep

- **WHEN** zbc minor bumps as part of this change
- **THEN** `ZpkgWriter.VersionMinor` and the Rust `ZPKG_VERSION_MINOR` are
  incremented by exactly 1 in the same commit; `docs/design/runtime/zpkg.md`
  Minor changelog has an entry citing this spec as the trigger

#### Scenario: golden fixtures regenerated

- **WHEN** zbc / zpkg minor changes
- **THEN** all 6 zbc-format fixtures and 4 zpkg-format fixtures are regenerated
  (via `src/tests/zbc-format/generate-fixtures.sh` and
  `src/tests/zpkg-format/generate-fixtures.sh`); FormatGoldenTests pass with
  the new bytes

### Requirement: workaround cleanup

#### Scenario: Sha256 drops `_zeroBytes` helper

- **WHEN** this change is applied
- **THEN** [`src/libraries/z42.crypto/src/Sha256.z42`](../../../../../src/libraries/z42.crypto/src/Sha256.z42)
  no longer contains `_zeroBytes`; every `new byte[N]` site uses the bare allocation;
  all SHA-256 NIST test vectors still pass

#### Scenario: Dictionary drops explicit `bool[]` zero loops

- **WHEN** this change is applied
- **THEN** [`src/libraries/z42.core/src/Collections/Dictionary.z42`](../../../../../src/libraries/z42.core/src/Collections/Dictionary.z42)
  no longer contains explicit `while (i < N) { this.occupied[i] = false; ... }`
  init loops for the `bool[] occupied` arrays in both the constructor and `Grow()`;
  all stdlib Dictionary tests still pass

## IR Mapping

`Instruction::ArrayNew` extended with an **element type tag** carried in the
zbc encoding for that opcode. The IR-level record `ArrayNewInstr` adds a
`Z42Type ElemType` field at codegen time (or equivalent tag). See
[design.md](../../design.md) for the concrete byte-level encoding.

## Pipeline Steps

- [ ] Lexer — n/a (no syntax change)
- [ ] Parser / AST — n/a
- [ ] TypeChecker — n/a (`BoundArrayCreate.ElemType` already populated)
- [x] IR Codegen — `FunctionEmitterExprs.VisitArrayCreate` passes element type into `ArrayNewInstr`
- [x] IR module — `ArrayNewInstr` carries element type
- [x] zbc Writer — emits element type tag byte after `size` register
- [x] zbc Reader (Rust + C#) — decodes the new byte; constructs `Instruction::ArrayNew` with element tag
- [x] VM interp — `exec_array::array_new` uses `default_value_for(...)` keyed on element tag
- [x] JIT helper — `jit_array_new` uses the same path (helper signature updated to pass element tag)
- [x] Version bump — `ZbcWriter`, `ZbcReader`, `ZpkgWriter`, `ZpkgReader` minor++; changelog entries; fixtures regen
