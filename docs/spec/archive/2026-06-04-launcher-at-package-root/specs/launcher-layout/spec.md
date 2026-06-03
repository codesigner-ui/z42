# Spec: launcher package layout (trampoline at root)

## ADDED Requirements
### Requirement: portable resolution with trampoline at package root
#### Scenario: bare run from package root
- WHEN `<pkg>/z42`(根)无参运行,且 `<pkg>/bin/z42vm` + `<pkg>/launcher.zpkg` 存在
- THEN trampoline 解析 portable runtime(vm=bin/z42vm, core=launcher.zpkg, libs=libs),exec launcher.zpkg(输出 help)
#### Scenario: run app with args
- WHEN `<pkg>/z42 app.zpkg -- a b`
- THEN 经 launcher.zpkg 运行 app,argv `a b` 透传
#### Scenario: installed mode unaffected
- WHEN `$Z42_HOME/launcher/{z42vm,launcher.zpkg,libs}` 存在
- THEN installed 分支仍优先命中(布局不变)
