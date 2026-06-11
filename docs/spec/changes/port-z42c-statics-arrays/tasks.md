# Tasks: port-z42c-statics-arrays

> 状态：⚪ DRAFT 待审批 | 创建：2026-06-12 | 子系统锁：z42c

- [ ] SA-1 new T[n]（parser→Bound→ArrayNewInstr 0x80 链）+ 单测
- [ ] SA-2 静态读（BoundStaticGet→StaticGetInstr 0x62）+ 单测
- [ ] SA-3 __static_init__ 合成（函数表首位；StaticSet 0x63）+ 单测
- [ ] SA-4 sacheck 第 7 zbc 源（byte-compare 7/7）+ z42c.core 自编译冒烟 gate 步 + 文档 + commit
