# Proposal: instance method binding must be receiver-aware

## Why

The C# bootstrap compiler resolves instance method calls (`receiver.Method(args)`)
to a target function name during codegen. The current logic in
[FunctionEmitterCalls.cs:107-127](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs#L107-L127)
falls through to a global `DepIndex` lookup whenever the receiver's class
is NOT in `ImportedClassNamespaces`. The DepIndex is keyed by method name
+ arity, so if **any** imported stdlib class has a method with the same
name+arity, the call is silently rebound to that stdlib method — even
when the actual receiver class declares its own method with that name.

**Concrete reproduction** (verified 2026-05-14):

- `Std.Toml.TomlValue` declares `bool ContainsKey(string key)`,
  `TomlValue Get(string key)`, `void Set(string key, TomlValue v)`.
- `Std.Collections.Dictionary<K, V>` declares `bool ContainsKey(K key)`,
  `V Get(K key)`, `void Set(K key, V value)`.
- Inside `TomlParser._parseKeyValue`:
  ```z42
  TomlValue target = dst;
  if (target.ContainsKey(seg)) { ... }
  ```
  Emitted IR:
  ```
  %20:bool = call @Std.Collections.Dictionary.ContainsKey  %11, %19
  ```
  Static `call` to **Dictionary**'s method, not `v_call` against
  TomlValue's vtable. Dictionary's method body then runs against a
  TomlValue instance and fails — `VCall: function 'Std.Toml.TomlValue.FindSlot'
  not found` (FindSlot is Dictionary's internal method, doesn't exist
  on TomlValue).

**Scope of impact**:

- z42.toml's TomlParser breaks whenever it touches its own value type's
  Get/Set/ContainsKey methods. Currently masked because z42.toml is the
  only L1 stdlib package that defines a class with these stdlib-method
  names internally. (The user-facing tests pass — they bind in a fresh
  CU where namespace resolution differs.)
- **z42.json adds the bug back catastrophically**: JsonValue has
  Get/Set/ContainsKey/Length/Count/At/Add/Keys, which collide with
  TomlValue + Dictionary + List. Without this fix, neither z42.json
  nor z42.toml work after both are loaded.
- **Any future stdlib package** defining a class with method names
  shared with z42.core's collections (List / Dictionary / Stack /
  Queue) hits the same bug. Common names like `Add`, `Get`, `Count`,
  `Length`, `ContainsKey` are minefields.
- The "L3-G4d" guard at line 112-113 only catches the case where the
  receiver's **class name** clashes with an imported class. It does
  nothing about **method-name** clashes between distinct classes.

## What Changes

Replace the imported-class-namespace guard with a **receiver-aware**
check: prefer the receiver's own class methods (and its inherited base
methods) over a name-only DepIndex match. If the receiver's class
declares (or inherits) the method, dispatch via `v_call` so the
receiver's vtable wins. If neither the receiver's class chain nor
DepIndex has the method, fall through to the existing v_call fallback
(which handles unknown receivers / interface dispatch).

Specifically in
[FunctionEmitterCalls.cs `EmitInstanceBoundCall`](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs):

```csharp
// BEFORE (line 112-115):
bool receiverIsLocalClass = call.ReceiverClass is not null
    && !_ctx.ImportedClassNamespaces.ContainsKey(call.ReceiverClass);
if (!receiverIsLocalClass && _ctx.DepIndex.TryGetInstance(...)) { ... }

// AFTER:
// Receiver's class (or any ancestor) declares this method? → v_call wins.
bool receiverClassOwnsMethod = call.ReceiverClass is not null
    && ReceiverChainHasMethod(call.ReceiverClass, call.MethodName!);
if (!receiverClassOwnsMethod && _ctx.DepIndex.TryGetInstance(...)) { ... }
```

`ReceiverChainHasMethod` walks the `ClassRegistry` from
`QualifyClassName(receiverClass)` up the base chain, returning true if
any class in the chain has the method name in its `Methods` set.

The DepIndex shortcut remains for receivers where ClassRegistry has no
entry (Unknown type from `var` inference, primitive receivers whose
method routing differs, etc.).

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|------|---------|------|
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs` | MODIFY | Replace `receiverIsLocalClass` check with `receiverClassOwnsMethod` walking the inheritance chain via `ClassRegistry`. Update the comment to reflect "receiver-aware" rationale. |
| `src/compiler/z42.Tests/FunctionEmitterCallsTests.cs` (or similar) | NEW or MODIFY | Unit test: receiver class with own `ContainsKey` method must not be hijacked by Dictionary's. |
| `src/tests/zbc-format/instance-method-binding-receiver/` (or in golden tests) | NEW | Golden test: two classes with same method name, one in user code, one in stdlib — call site emits v_call to user's class. |
| `docs/design/compiler/compiler-architecture.md` | MODIFY | Document the receiver-aware binding rule in the codegen section. |

**只读引用**：
- `src/libraries/z42.toml/src/TomlParser.z42` — current victim (already works around it via `(TomlValue)x` casts; some calls still hit the bug)
- `src/libraries/z42.json/` — recovery target after this lands

## Out of Scope

- **Re-shipping z42.json** — separate spec follow-up that recovers
  z42.json once this fix lands. Drops the `Json*` method-name prefix
  workarounds.
- **Static method binding** — the bug only affects instance methods
  (`call`). Static method binding has its own path (see
  `EmitStaticBoundCall` if it exists) and is not changed here.
- **DepIndex itself** — keep as-is; it still serves as the
  name→qualified-function fallback when receiver class isn't known
  statically.
- **`v_call` runtime dispatch** — unchanged; the runtime already
  dispatches v_call via receiver's vtable correctly.

## Open Questions

- [ ] Should the receiver-chain walk also include implemented interfaces?
      For now, **no** — interfaces use a separate `EmitVirtualBoundCall`
      path (already correct). Interfaces declared by a class don't add
      methods to its `Methods` set (separate registry).
- [ ] What about generic class methods (`List<T>.Get`)? The receiver
      class name is the qualified generic name; ClassRegistry should
      already key by qualified IR name (with `$N` arity suffix when
      mangled). Verify via a generic-class test.
- [ ] Performance: chain walk is O(inheritance-depth). All real classes
      are at most 2-3 deep (Exception subclasses, etc.); cost is
      negligible. No caching needed.
