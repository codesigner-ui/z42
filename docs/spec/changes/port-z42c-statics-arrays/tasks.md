# Tasks: port-z42c-statics-arrays

> 状态：🟢 已完成（core 自编译冒烟移交 follow-up——见实施记录）| 创建+批准+实施+归档：2026-06-13 | 子系统锁：z42c（已释放）

- [x] SA-1 new T[n]（parser→Bound→ArrayNewInstr 0x80 链）+ 单测
- [x] SA-2 静态读（BoundStaticGet→StaticGetInstr 0x62）+ 单测
- [x] SA-3 __static_init__ 合成（函数表首位；StaticSet 0x63）+ 单测
- [x] SA-4 sacheck 第 7 zbc 源（byte-compare 7/7）+ z42c.core 自编译冒烟 gate 步 + 文档 + commit

## 实施记录（2026-06-13）

**sacheck（静态类常量 + new int[n] + 下标/Length + oracle）FULL-FILE byte-identical（3453B）+ 执行正确**——zbc 对账升 7/7。链：`new T[n]`（parser→BoundArrayNew→ArrayNewInstr 0x80）+ 静态读（裸类名.字段→BoundStaticGet→StaticGet 0x62）+ `{stem}.__static_init__`（IrGen 函数表首位合成；每静态字段 init→StaticSet 0x63；VM 加载期执行）+ arr.Length（C# 字节实证=FieldGet "Length" 非 ArrayLen 指令！）。顺带修 2B 期挂账：泛型注解 var-decl `Box<int> b=` 平衡尖括号 lookahead。

**字节五连真相（全靠 dump，几条反直觉）**：
① **arr.Length = FieldGet obj,"Length"**（非独立 ArrayLen 指令；VM 对数组特判）——PS-2 当初的 ArrayLen 指令是错的，e2e 顺延才暴露
② **静态类 TYPE 描述符须含静态字段块**（zbc 1.13：classFlags 后 staticFieldCount+条目；InternPoolStrings 实例字段后 intern 静态名/类型）——IrClassDesc 新增 StaticFields，_classDesc 按 FieldSymbol.IsStatic 分流
③ **__static_init__ 无 LineTable**（DBUG file 不在此入池——TrackLine 不能加，否则 sa.z42 早入池乱序）
④ **StaticGet/ArraySet 结果寄存器 = I32**（元素/字段真实类型），但 **数组局部 + ArrayGet 中间值在 REGT 的 tag 微妙**：数组局部 reg=Ref(0x0e)、StaticGet dst=Unknown、ArrayGet dst=I32（FUNC 指令 tag）——逐位校准
⑤ **并行流当日 bump zbc 1.16/zpkg 0.18**（add-reflection-array-element-type：ArrayNew 追加 elem FQ 名 string idx）——本 change 顺带同步（编码+intern+版本+empty/f5 golden 重截+header 单测 0x10/0x12）

driver 增诊断详情打印（ErrorCount>0 时逐条 file(line,col): code: msg——真实改进）。

**spec 偏差（移交 follow-up）**：z42c.core 自编译冒烟未达成——盘点时只见 G3/G4，全包编译后水落石出还有**跨类静态方法调用**（`Diagnostic.Error(...)` 工厂模式）等新缺口（超本 change 范围）。core 自编译留下一轮 gap-batch（port-z42c-static-calls 等）。
