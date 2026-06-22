# stdlib API 设计准则

跨包通用的**接口面设计**规矩（与 [`overview.md`](overview.md) 的"实现层级/extern 预算"正交——
那管"实现住哪一 Tier"，这管"对外暴露多少接口、怎么暴露"）。新加 stdlib 接口时遵循。

---

## 准则 1：Source × Operation 漏斗 —— 避免 m×n 接口爆炸

### 反模式

把"数据从哪来"（source：文件路径 / 字节数组 / 流 / 网络…）和"对数据做什么"（operation：
读全文 / 读行 / 读全字节 / parse…）耦进同一函数签名（甚至函数名），注定 **m×n** 接口：

```z42
// ❌ 每加一种源 × 每种操作都新开一个接口
string ReadTextFromFile(string path);
string ReadTextFromBytes(byte[] data);
string ReadTextFromStream(Stream s);
string[] ReadLinesFromFile(string path);   // …继续乘
```

### 准则

**别把 source 编进操作的签名/名字；把 source 做成一个"值"传进来。** 让所有 source 汇聚到
**同一抽象**，每个 operation **只对这个抽象写一次**：

- m 个 source → m 个**适配器/构造器**（把自己变成该抽象）
- n 个 operation → n 个**只接该抽象**的函数
- 总数 **m + n**，不是 m×n

operation 应"接受最一般的类型"（抽象本身），调用方有任何源都能用。

### z42 已落地的范例：`Std.IO.Stream`

这条准则在 z42 stdlib **已是事实标准**，见 [`io-stream.md`](io-stream.md)：

- **唯一字节漏斗** `Stream`；所有源各实现一次：`FileStream` / `MemoryStream(byte[])`（零拷贝只读视图）/
  `NetworkStream` / `ProcessOutputStream` / 压缩流…
- **operation 只写一次**：`ReadAllBytes()` / `ReadExactly(n)` / `CopyTo(dest)` 在 `Stream` 基类上，
  子类不重写。
- **编码轴单独收掉**（见准则 2）：`StreamReader(Stream, Encoding)` / `TextReader` 在 Stream 上叠 char 层。

新加"从某种源读/写"能力时：**实现一个 `: Stream` 适配器**，复用全部既有 operation——不要新开一组
`XxxFromYyy` 接口。

---

## 准则 2：正交轴各自收敛，别相乘

`文本 = 字节 + 编码`。若写 `ReadTextUtf8` / `ReadTextAscii × 每种源`，编码就成第三根轴（m×n×k）。
正解是把每根正交轴**各收敛成一个参数/一层**，而非乘进函数名：

- 编码 → `Encoding` 参数 / `TextReader` 层（不是 `...Utf8` / `...Ascii` 后缀）。
- 格式（json/csv/text）→ 解析器**也接 `Stream`/`string`**（`Json.Parse(Stream)` / `Json.Parse(string)`），
  绝不为"从文件 parse json""从字节 parse json"各开一个——漏斗思想**递归适用**。

---

## 准则 3：便利糖是薄委托，不是重复逻辑

高频组合（如"文件读全文"）保留一行便利版（人体工学），但守纪律：

- 便利函数/重载是 **2 行委托**（适配器 + 核心 operation），**不重复任何逻辑**；
- **只给少数主路径**铺糖，**不铺满 m×n 网格**；
- 逻辑永远只活在 m+n 那层。

> z42 已遵循：`BinaryReader(byte[])` 保留为便利构造器，内部**委托** `new MemoryStream(...)`；
> 新增 `BinaryReader(Stream src)` 让用户接任意流（见 [`io-binary.md`](io-binary.md)）。罪不在重载/便利，
> 在每个源重新实现一遍逻辑。

---

## 与语言演进的关系

- **现在（L1/L2）**：抽象载体用 **`class` 基类 / `interface`**（如 `Stream`）——无需泛型，现在就能做。
- **以后（L3）**：traits/泛型落地后可演进成 Rust 式 `Read` **trait** + 泛型/默认方法 operation；
  接口/基类模型**前向兼容**，按 `io-stream.md` 已记的迁移路径平滑升级，不返工。
