# F17 NX2506 实体倒角 API 笔记

这些是独立 API 探索事实，不是产品验收。`Features.ChamferBuilder` 提供 `Option` 的 `SymmetricOffsets`、`TwoOffsets`、`OffsetAndAngle`，`Method.EdgesAlongFaces`、`ReverseOffsets`、`SmartCollector` 和三个表达式。已提交特征可通过 `CreateChamferBuilder(feature)` 回读；以原生 mode 和实际物理截距共同确认，不用双距离冒充距离角度。

第一距离 2 mm、第二距离 4 mm、角度 30°。等距均为 2/2；双距离 false 为 x=4、y=2，true 为 x=2、y=4；角度 false 为 x=1.15470053837925、y=2，true 为 x=2、y=1.15470053837925，即另一截距 `2*tan(30°)`。读回角度单位为 Degrees，距离为 MilliMeter，派生表达式不得误认为输入：等距 second/angle 回读为 2/45°，双距离 angle 回读为约 26.565°，角度模式 second 回读为 1.15470053837925。collector 仍含输入 tag，JID 在提交后简化；不能将 tag 或简化 JID 当跨会话身份。

**邻面返回顺序不足以决定 first 截距方向。** 例如 X 边 y=0,z=0 的顺序为 [y,z]，X 边 y=0,z=12 为 [z,y]，但两者 first 都沿 z；Z 边不同角则还会变换 first 的物理轴。不得把这些样本推广为所有拓扑或 NX 版本的规律。方向必须以可验证方法确定，并在实际结果上验证；若不可证明，应拒绝或真实回滚，而不能静默交换参数。

API 探针零件只用于探索，非产品 SaveCopy 重开证据。getter、历史输入 suppressed、保存重开及其它形状仍须产品专门验证。

本次 getter 后该副本 dirty=false，关闭时未保存；这是一次实测，不推导所有 getter 或零件均无副作用。Python 接口检查了 collector 数量和规则类型；跨会话身份不使用 Tag 等值。首轮独立高角执行 APPLIED 但测试脚本错误地要求必须翻转而失败，不能计为原生通过，installed 的高角是独立成功样例。
