# F15 NX2506 API 与原生语义

以下为本机探针事实；产品事务、fresh和安装验收已完成，另见[F15](F15.md)，不能把探针计入产品案例。

使用 `Part.Features.CreateBooleanBuilderUsingCollector`、显式 `ScCollector` 和 `CreateRuleBodyDumb` 配置目标与工具。新 builder 的 collector 可能为空，必须创建并赋值。三种 `Feature.BooleanType` 为 Unite、Subtract、Intersect；明确 `RetainTarget=false`、`RetainTool` 按请求、`ConvertToSew=false`。提交后应重新建立 builder 读回实际定义，结果用 `Feature.GetBodies()` 完整枚举。

**本机 `CopyTools` 与 `RetainTool` 联动，是同一保留状态。** 不能先设置 RetainTool=true 后再写 CopyTools=false，也不能在读回时把 CopyTools=true 当成额外不支持的复制模式。初版实现曾误当作独立开关，导致 r02/r03 保留工具案例回滚；见下方诊断证据。

## 原生探针

| 探针 | 观察 |
| --- | --- |
| 三运算 × retain false/true | 目标 10×10×10 mm，工具沿 X 偏移 5 mm；unite 1500 mm³，subtract/intersect 500 mm³；工具保留或从当前实体清单消费，远处 64 mm³ 实体保持 |
| 消费工具关联 | 工具不再出现在当前 part.Bodies 中，但重建 BooleanBuilder 的工具 collector 仍能读回原始工具引用；不能用当前实体清单替代历史输入关联 |
| 分裂 subtract | 目标 X=[0,10]，工具 X=[3,7] mm，输出两个实体 X=[0,3]、[7,10]，各 300 mm³ |
| 多工具 intersect | 目标 X=[0,10]、工具 X=[0,7] 与 [3,10] mm，结果仍 1000 mm³，支持 T∩(U1∪U2) 语义；所有体 Y/Z 均 [0,10] |
| 无交 subtract | NX 可以创建特征并消费工具，目标不变；产品要求检测无实际效果并回滚 |
| 无交 intersect、完全覆盖 subtract | 探针中 NX 提交抛异常，不据此声称已验证产品恢复 |
| 完全覆盖 intersect | NX 可消费工具但目标不变，同样需要产品无作用拒绝 |

正式多工具相交验收使用工具并集只覆盖目标一部分的构型，并与逐个共同交集产生不同体积，以同时证明实际效果及集合语义。

## 探针失败记录

这两轮均未进入布尔 Commit，不算生产产品失败或通过。原始文件保留。

## 产品失败定位与属性联动

r02 首个真实产品案例在工厂提交后匹配失败。

修正配置和读回对同一属性的理解，不放宽对象身份检查。
