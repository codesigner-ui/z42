# z42.yaml

YAML 1.2 subset reader / writer. Pure-script stdlib mirroring
`z42.toml` and `z42.json`.

## Public API

```z42
using Std.Yaml;

YamlValue v = Yaml.Parse(text);   // string → YamlValue tree
string out  = Yaml.Stringify(v);  // YamlValue tree → block-style YAML
```

`YamlValue` is a discriminated-union scalar / sequence / mapping with
`Is*()` predicates, `As*()` accessors, and `Get / At / Add / Set` for
nested traversal. See [docs/design/stdlib/yaml.md](../../../docs/design/stdlib/yaml.md)
for the full API + supported syntax + Deferred items.

## Quick example

```z42
using Std.IO;
using Std.Yaml;

void Main() {
    string yaml = "name: alice\nfriends:\n  - bob\n  - charlie\nage: 30\n";
    YamlValue v = Yaml.Parse(yaml);
    Console.WriteLine("name: " + v.Get("name").AsString());
    Console.WriteLine("age: "  + v.Get("age").AsInt().ToString());
    YamlValue friends = v.Get("friends");
    int i = 0;
    while (i < friends.Length()) {
        Console.WriteLine("- " + friends.At(i).AsString());
        i = i + 1;
    }
}
```

## Scope (v0)

- ✅ Block mapping / sequence with indentation-based nesting
- ✅ Flow mapping `{}` / flow sequence `[]`
- ✅ Plain / single-quoted / double-quoted strings (with `\n \t \"
  \\ \uXXXX` escapes)
- ✅ Scalars: `null` (`~`) / bool / int / float / string
- ✅ Comments
- ❌ Anchors / aliases / tags / multi-line `|` `>` / multi-doc /
  complex keys / hex-octal int / timestamps — see Deferred section in
  [yaml.md](../../../docs/design/stdlib/yaml.md#deferred--future-work)
