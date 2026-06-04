# Spec: xtask dev CLI

## ADDED Requirements
### Requirement: unified dev CLI via xtask.zpkg
#### Scenario: help
- WHEN `z42 xtask.zpkg --help` (or no args)
- THEN prints the CLI reference (build/test/run/deps/bench)
#### Scenario: dispatch build
- WHEN `z42 xtask.zpkg build stdlib`
- THEN cd's to repo root and runs the stdlib build, forwarding exit code
#### Scenario: unknown command
- WHEN `z42 xtask.zpkg bogus`
- THEN prints "unknown command 'bogus'" + help, exits 2
#### Scenario: unknown build target
- WHEN `z42 xtask.zpkg build nope`
- THEN prints "xtask build: unknown target 'nope'", exits 2
