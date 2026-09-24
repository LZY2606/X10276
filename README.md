# EPUB 阅读序谱离线审阅应用

一套纯离线的 C# / ASP.NET Core / SQLite / SVG 应用，用于数字出版团队导入 OCF/EPUB 容器，并沿同一条审阅叙事检查 package manifest、spine、navigation、media-overlay、fallback 与资源引用图。

## 运行

```bash
dotnet restore
dotnet test
dotnet run --urls http://127.0.0.1:5374
```

打开 `http://127.0.0.1:5374` 即进入内置样例审阅页，不需要账号、云服务或额外配置。SQLite 数据库默认位于 `EpubReview.Web/data/epub-review.sqlite`，首次启动自动生成。

## 离线案例包

领域样例由 `EpubReview.Web/Domain/SampleCase.cs` 直接构建为 OCF ZIP，包含：

- 多 package：`META-INF/container.xml` 同时声明 `content.opf` 与 `alt.opf`。
- fallback：一条循环链和一条缺失目标链分开报告。
- fragment：有效、缺失和跨 package 锚点。
- 路径越界：`../../outside-container.xhtml#forbidden` 不允许规范化出容器。
- 路径候选：`other/../data/note.txt` 与重复 ZIP 条目保留全部合法候选。
- spine：重复 `chapter1`，且 spine 只作为主阅读顺序，不代替资源引用全集。
- media-overlay：manifest 指向不存在的 overlay，SMIL 内还有缺失音频。
- 未识别 metadata：两个 package 都有非 OPF/Dublin Core 命名空间元素并原样往返。

种子工作链包含初始导入、基线分析、参数修订、仅影响局部依赖的源数据修订、互相冲突的人工决定。每个分析事件记录输入 SHA-256、参数版本、父依赖版本和新依赖版本。

## 分层

- 不可变材料：ZIP 整包字节、每个 ZIP 条目的原始字节、原始路径、media type、fragment、未识别 metadata。
- 派生结果：规范化路径、资源图、阅读顺序、导航树、fallback 步骤、overlay 边、诊断和判断依据。
- 事件记录：导入、参数修订、源材料修订、人工接受/否定/调整及每次计算均形成独立事件。
- 导出：`/api/export` 生成离线 `.zip`，内含项目 JSON 与全部材料字节；`/api/import` 按原身份、顺序、分支和决定历史导入。

## 结果类型

业务结果明确分成：

- `Completed`：可完成计算。
- `BudgetExhausted`：资源预算耗尽。
- `FormatCorrupt`：ZIP/XML/OCF 格式损坏。
- `SemanticallyUnresolvable`：fallback 循环、路径越界语义冲突或互相矛盾的人工决定等。

所有诊断携带代码、级别、位置、行/列（可用时）、稳定对象身份和结果类型，不用空值或默认值伪装成功。

## SVG 与身份

页面展示阅读顺序、资源依赖图、导航树、断链锚点、fallback 候选、media-overlay、未知 metadata、事件时间线和机器可读判断依据。所有显示值都带稳定身份；SVG 显式展示坐标基准、单位、有效区间和失效原因。
