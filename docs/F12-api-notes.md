
# F12 NX2506 API 核对

以下静态 API 证据不能代替产品验收。

| 本机 API 来源 | 语义与设计影响 |
| --- | --- |
| `Features.ExtrudeBuilder.FeatureOptions`；`GeometricUtilities.FeatureOptions.BodyType` | 使用 `BodyStyle.Solid`／`BodyStyle.Sheet` 显式指定输出，setter 需要 solid_modeling 许可。原生枚举存在不等于开放轮廓产品路径已验证。 |
| `GeometricUtilities.Extend.ExtendType.ThroughAll` | 贯穿应使用原生限界类型，不能以很大的有限距离冒充。实际起止、方向与目标需产品回读。 |
| `Section.SetAllowRefCrvs(bool)` | F12 whole-sketch 选择显式排除 reference 曲线。 |
| `Section.GetOutputCurves(out NXObject[])` | 返回内核创建的 Section 曲线，区别于源曲线。官方说明不得修改或删除这些曲线，否则可能损坏模型／使会话不稳定。生产只能读取比较，通过所属 builder／Section 正常生命周期清理，不能逐条删除输出曲线。不能用输出 Tag 等于源 Tag 作选择证明。 |

## 原生探针与产品验证

- 整草图为外矩形、圆内环、第二矩形岛和一条 reference 开线。10 条源中实际 Section 输出 9 条、3 loops；reference 开线被排除。
- 有限区间 [-2, 8] 的 solid create 返回 2 个实体，体积分别约 3874.336293856409、1000 mm³，与 `(400−4π)×10`、`100×10` 一致。
- 12 mm 开线，显式 Sheet、范围 [-2, 5]，创建 1 个非实体体；此探针尚未测其面积和精确边界，不冒称完整片体验证。
- 10×10×15 mm 目标以 2 mm 宽贯穿条带切割，原生 feature.GetBodies 返回 2 个 solid，体积各约 600 mm³。
- 显式目标 unite 从约 1500 变为 1680 mm³；原生 ThroughAll、start=2 的 subtract 从约 1500 变为 960 mm³，EndExtend.TrimType 为原生 ThroughAll（Python 显示数值 6）。

探针的 Python UF bbox getter 报 AttributeError，AskRulesAndCollectedEntities 解包报 ValueError，均原样保留；不将这两项当作已观察几何。最终产品需独立回读 Section 域、边界／拓扑及结果。

必须以探针 JSON 判据判断成功，不能仅凭 runner 退出码。

开发 Journal 属于测试工具，不是 F22 产品 Journal 能力；root 单独串行操作 NX，不与 MCP 并行控制同一模型。

## F12 非目标体保全记录

`F12BodyGeometrySnapshot.cs` 记录当前会话内 Body/Face/Edge Tag、精确包围盒、实体一致性、面邻接／类型／原生参数／UV 域、5×5 曲面位置与一二阶导数、面面积；Parametric 面另存完整 UF Bsurface 的阶次、控制极点、节点和有理定义。边记录完整 Line／Arc／Ellipse／Spline 原生定义、参数域、17 点位置／导数及相邻面。Tag 仅用于同一加载事务，不当作保存重开的持久身份。

解析面／参数化 Bsurface 之外的曲面仍属于有限 UV 采样、边界和参数证据，不是任意曲面全域同形的数学证明。含 UV 尖点、退化边或不支持的原生边定义时可能无法读取有限导数，产品必须失败关闭，不能跳过关键字段或退回体积／box 比较。

首个恢复构建因 helper 输出写入占用失败，重试成功，不猜测占用来源；原日志保留。

继续提高至0.999999999没有进一步变化。F12高精测量与正式测试仍需集成实测；此探针不是产品案例。

NoDraft／NoOffset 默认模式虽然未激活，builder仍暴露 dormant 的2°前后draft和5mm尾offset参数。产品应显式设置普通无拔模／无偏置及零 dormant 参数，并验证有效表达式集合恰为本次请求允许的起止参数，不能用任意新增 FEATURE_EXPRESSION 关系自授权额外表达式。上述两个 batch 均真实退出0，未保存覆盖原模型、未强杀。

其UI默认数值不属于持久feature.GetExpressions。故产品提交前仍明确设置零值，提交后仅将这些inactive数值作诊断，不当作实际实体参数；NoDraft／NoOffset、有效起止参数及精确原生membership仍为严格门禁。

目标历史特征的 body_ids 从原体扩展为两个实际输出，但其 GetChildren 在 Commit 前后均为空，并没有反向列出新 EXTRUDE(14)。既有检查强制反向子项必须新增，因而拒绝并完整产品Undo；不是目标或非目标实体遭到未知形变。

当前仅据本机观察允许这一精确差异：既有特征必须是显式目标体的先前 owner，且新特征实际 GetParents 确认该父关系，才允许其 child 集合保持旧集合，或旧集合加本次新特征；不得删除既有 child 或新增其它 child。其他特征按原严格规则核对，新特征自身的原生父关系、显式 target、表达式、依赖与 body 输出保全继续独立验证。此修正仍须后续候选完整 split/unite 与独审实测闭环，不以诊断报告宣布成功。
