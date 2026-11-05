
# F11 NX2506 API 核对

来源为本机有效许可 NX2506 安装，未下载外部代码。

## 命令与对象

| 本机来源 | 观察 |
| --- | --- |
| 同文件第 82 行 | 恢复活动曲线／驱动尺寸，下游命令可使用活动曲线，活动尺寸控制草图几何 |
| 同类 `SelectAllProjectFeatureCurves` | 为 true 时可扩选同一 Project feature 的其它输出；F11 显式为 false |
| 同 XML 的 `Section.SetAllowRefCrvs(bool)` | 下游 Section 可显式控制是否接纳参考曲线；不应把某一设置推广为所有下游选择模式 |
| `NXOpen.UF.xml` 的 `UFSket.AskReferenceStatus` | 独立回读曲线参考状态 |

选择尺寸时，公共请求用 inspect 返回的 dimensional constraint JID；builder 的输入应为该约束的 `AssociatedDimension`。参考尺寸状态来自 `SketchDimensionalConstraint.DimensionState`。不能把尺寸约束 Tag、注释尺寸 Tag 与曲线 Tag 混作同一身份。

## 原生探索证据

Tag、线长及表达式值保持；Python 中 UF reference 数值为 0（Reference）/1（Active），DimensionState 为 1（Reference）/0（Driving）。生产 C# 使用命名枚举，不将这些数值写成外部契约。

它记录 Section 输出曲线端点以及清理后的 Part.Curves 库存，另做尺寸行为挑战：参考尺寸时，直接把线端点移至 14 mm 后未被拉回 10 mm；恢复 Driving 后把表达式改为 12，更新后线长为 12 mm。每个行为挑战均在独立 UndoMark 内撤销，不能将挑战导致的形变当成转换本身的输出。

参考曲线转换后，其关联尺寸仍为 Driving，线长仍为 10 mm；因此参考曲线并不等于退出草图求解。参考尺寸的 RHS 在端点移动后仍为 `10`，不能凭 RHS 声称当前测量值自动更新为 `14`；正式证据须分别记录原生尺寸测量与表达式。

恢复 Driving 并编辑表达式后，ComputedSize 与表达式 Value 均为 `12`。转换本身未改变所读的七项表达式标志。

此探针在转换前后读到 `Sketch.Status.NotEvaluated`、DOF=0；在受控 Driving 表达式编辑后读到 `UnderConstrained`、DOF=3。NotEvaluated 只表示该 getter 尚未给出已评估解，不能当作“已充分约束”或求解验证通过。F11 的求解行为须由受控挑战补证，不能写死某个 DOF 差值。

后续发现补证：

官方文档要求该评估在活动草图、未启用局部 Work Region 时进行。探针附带的 Fixed 约束没有制造过约束，不能将该记录计为冲突拒绝覆盖。
生产白名单必须精确识别这一变化，不能一律拒绝，也不能剥离所有表达式比较。
显式设置 EntireSketch 并 Commit 成功，且同一 builder 回读 EntireSketch。生产应在编辑事务内明确全草图求解范围，不能在只读 prepare 中创建／提交 Work Region。

Section 使用 CurveFeature rule 和 `SetAllowRefCrvs(false)` 时，输出数量为 4→3→4。`GetOutputCurves` 返回临时副本曲线，副本 Tag 不等于原草图 Tag；精确选择必须按几何一对一映射。Section 只用于测试下游选取，不在 prepare 中创建，也不构成 F12 实体拉伸能力。

这些是 API 探索记录，不计为生产验收案例。探针 `solver` 的 Python 字符串包含对象地址，不能作为最终可复算 solver 证据；正式验收应记录命名状态及数值 DOF。

它们均未进入转换矩阵，不是产品失败或产品通过证据。

## 运行与限制

它不是 F22 产品 Journal 功能。

API 发现不能代替生产或 installed 验收。

## 最终GUI求解条件与版本

最终生产候选0.11.0/31工具，M2Entry.dll 679424 bytes。这是本机受控观察，不声称证明NX内部实现原因。

生产每次求解保存该session偏好、临时设true并回读，finally恢复和回读；不写用户配置。恢复失败即使模型Undo成功仍为UNKNOWN。最终原生c3dc报告17/17及installed报告3的13/13补证该生产路径。UF AskSketchStatus已弃用且仅适用于legacy，未用它作为生产fallback；BeginTaskEnvironment的试验未解决问题，也未加入生产路径。开发Journal工具不是F22产品能力。
