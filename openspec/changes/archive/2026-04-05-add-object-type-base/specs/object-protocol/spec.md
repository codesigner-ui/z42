# Spec: Object Protocol and Type Descriptor

## ADDED Requirements

### Requirement: GetType() returns a Type descriptor

#### Scenario: GetType on a user-defined class
- **WHEN** `obj.GetType()` is called on an instance of class `Foo` in namespace `bar`
- **THEN** returns a `Type` object where `Name == "Foo"` and `FullName == "bar.Foo"`

#### Scenario: GetType on a top-level class (no namespace)
- **WHEN** `obj.GetType()` is called on an instance of class `MyClass` (no namespace)
- **THEN** `Name == "MyClass"` and `FullName == "MyClass"`

---

### Requirement: ReferenceEquals distinguishes identity

#### Scenario: Same reference
- **WHEN** `Object.ReferenceEquals(a, a)` where `a` is a non-null object
- **THEN** returns `true`

#### Scenario: Different instances of same class
- **WHEN** `Object.ReferenceEquals(new Foo(), new Foo())`
- **THEN** returns `false`

#### Scenario: Both null
- **WHEN** `Object.ReferenceEquals(null, null)`
- **THEN** returns `true`

#### Scenario: One null
- **WHEN** `Object.ReferenceEquals(a, null)` where `a` is non-null
- **THEN** returns `false`

---

### Requirement: Equals defaults to reference equality

#### Scenario: Same instance
- **WHEN** `a.Equals(a)` for any non-null object
- **THEN** returns `true`

#### Scenario: Distinct instances
- **WHEN** `a.Equals(new Foo())` where `a` and `new Foo()` are different allocations
- **THEN** returns `false`

---

### Requirement: GetHashCode returns identity-based hash

#### Scenario: Same object, consistent hash
- **WHEN** `a.GetHashCode()` is called twice on the same object
- **THEN** both calls return the same `int` value

#### Scenario: Different objects, typically different hashes
- **WHEN** `a.GetHashCode()` and `b.GetHashCode()` on two separately allocated objects
- **THEN** the result is an `int` (no crash); values may differ

---

### Requirement: ToString defaults to type name

#### Scenario: Unoverridden ToString
- **WHEN** `obj.ToString()` is called on a class that does not override `ToString`
- **THEN** returns the unqualified class name (e.g. `"Circle"`)

## MODIFIED Requirements

### Object.Equals signature
**Before:** `public virtual bool Equals(Object other)` — non-nullable, crashes on null input
**After:** `public virtual bool Equals(Object? other)` — nullable, returns false for null

### Object.ToString default
**Before:** `public virtual string ToString() => ""` — returns empty string
**After:** `public virtual string ToString() => GetType().Name` — returns type name

## Pipeline Steps
- [ ] Lexer — no change
- [ ] Parser / AST — no change
- [x] TypeChecker — NativeTable entries required for extern validation
- [x] IR Codegen — EmitNativeStub used for GetType / ReferenceEquals / GetHashCode
- [x] VM interp — three new builtins in builtins.rs
