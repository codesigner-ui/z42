# Spec: statics-arrays

#### Scenario: 数组创建+使用
- **WHEN** `int[] xs = new int[3]; xs[0]=40; xs.Length`
- **THEN** 0 错；ArrayNew 0x80 字节与 C# 一致；执行正确

#### Scenario: 静态常量
- **WHEN** `static class Sev { public static int Error = 2; } ... Sev.Error`
- **THEN** StaticGet 读取；__static_init__ 合成于函数表首位；执行正确

#### Scenario: 对账+自举首包
- **WHEN** sacheck 双编译；z42c build z42c.core
- **THEN** byte-compare 7/7；core 7 文件 0 错产 zpkg（gate 常驻冒烟）
