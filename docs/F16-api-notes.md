# F16 NX2506 API 笔记

状态：API 探针及产品原生/installed 定向验证完成。API 探针不等同于产品验收。

本机 NX2506 文档可见 `Part.Features.CreateEdgeBlendBuilder`、`AddChainset(ScCollector, string)`、`GetNumberOfValidChainsets`、`GetChainset` 和 `EdgeDumbRule.GetData`。实际链集索引、提交后的输入边关联以及保存重开行为须依实测确认，不能仅由 API 名称推断。

## 本机探针事实

20×16×12 mm 长方体，单边和两条不相邻平行边，半径 2 mm；创建原生 `BLEND`，父特征 `BLOCK`。

- `AddChainset` 返回 0；重开 builder 的有效 chainset 数为 1，索引 0 有效，1、2 拒绝。半径表达式数值 2、公式 `2`、单位 `MilliMeter`。
- `GetChainsetAndSectionValue` 对应 Circular；每条输入边的 variable radius 四数组均为空。产品仍须实际读取并拒绝不支持的定义，不能只凭本次默认值假设所有特征相同。
- **提交后输入边被 suppressed。** collector 保留原输入 Tag，但 JID 简化，例如 `EDGE * 1 * 2`；对历史输入直接 `GetVertices` 抛出 `Operation forbidden on suppressed object`。必须在 prepare 阶段保存实际输入几何，再由原生选择规则关联核对，不能把当前结果边当历史输入。
- 输出面记录包含圆柱半径、轴和边。探针工作件读取前后 dirty 均为 true，不能由此断言 getter 是否改变 dirty。
- Python 返回通用 SelectionIntentRule 包装；C# 的具体 rule 类型/数据读取以产品实测为准。

产品几何夹具为 20×12×10 mm，与本探针不同，最终案例数不得混加。探针后正常退出且观察无 NX/helper/journal 残留。

## 产品与持久化事实

产品实际 C# `EdgeDumbRule.GetData` 已在 r02 原生和 installed 成功执行，并严格比较规则边与 collector、准备输入。产品只接受单 chainset、Circular、空 variable radius 数据；传播/复杂修补选项受固定配置和回读限制。完整几何域和门限见 [F16](F16.md)。

installed 的两个不同特征可同时出现相同的简化历史输入 JID（例如 `EDGE * 2 * 3`）。保存重开不得把它当全局持久对象 ID，实际以 feature 及不同 parent/output 关联区分；跨会话 Tag 不比较。SaveCopy 对无法按支持域回读的旧 EdgeBlend 明确拒绝，不返回空定义假装保全。

保存副本的补充 NX Python 审计，材料点入口是 `uf.Modeling.AskPointContainment`，坐标每项须为 float；`uf.Modl` 没有同名方法。两次脚本 API 错误原始 FAIL 保留，修正后对同一保存文件只读重开通过，未重放建模。重开读取使副本 dirty，审计不保存并正常关闭；这与 API 初始探针前后均 dirty 的情况不同。
