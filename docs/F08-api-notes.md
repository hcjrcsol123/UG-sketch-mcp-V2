
# F08 NXOpen API 使用要点

## 调用与方向

成功调用顺序：CreateSketchOffsetBuilder(SketchOffset.Null)，显式设置 CapType、ConvertToReference=false、CreateConstraint=false、CreateDimension=false、IsSymmetric=false、NumberOfCopies=1、Distance.RightHandSide；CreateSection，限制 OnlyCurves、AllowSelfIntersection(false)；CreateRuleCurveDumb(显式曲线数组)，AddToSection 后 UpdateLoopsAndCopies，再按需 ReverseOffsetDirectionOfChain，EvaluateOffset、GetOutputCurvesOfOffset、Commit、再次读回，Destroy builder。

Section.SetStartAndDirection 对探针开放链不适用；不能依赖它或输入顺序决定默认 side。原生预览的 GetOutputCurvesOfOffset 已返回可读 Curve；本探针未验证预提交 GetAllGeometry 的可见性，不作这方面声明。

## 独立数值结果与限制

- 折线 (0,0)→(20,0)→(20,10)，d=2 mm：原生正距 Extension 输出 (0,−2)→(22,−2)→(22,10)；负距输出 (0,2)→(18,2)→(18,10)。前者相对原遍历为 right。
- 同折线的正距 Arc 连接为两直线和中心 (20,0)、半径 2 mm 的四分之一圆，连接 (20,−2) 与 (22,0)。负距 Arc 连接却额外产生半径 0、参数域 0 的退化 Arc。
- CCW 矩形 [0,20]×[0,10] 正距 d=2 mm 的 Extension 为 [−2,22]×[−2,12]；负距为 [2,18]×[2,8]。反转链输入不改变原生默认外侧。
- 整圆 r=10 mm 正距变为 12 mm，负距 8 mm；反转方向输入也不改变默认外侧，ReverseOffsetDirectionOfChain 能翻转。
- 相切线弧链：线 (−10,0)→(0,0)，圆弧中心 (0,5)、r=5、参数 270°→360°，再线 (5,5)→(5,15)。Extension 正距得到水平线 y=−2、同心弧 r=7、竖线 x=7；负距得到 y=2、r=3、x=3。Arc 模式会在两个相切节点各生成一条 r=0 弧。两侧 Extension 未新增约束。

产品必须依据请求遍历和草图法向验证 side，不能机械映射原生距离符号；必须核对所有输出曲线及实际闭合性，拒绝或真实恢复退化输出。探针中的 passed 只记录调用完成及新增对象存在，不是几何合法性或 F08 验收结论。

源曲线参考状态可用 `UFSession.Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status)` 实读。本机随附 `UGOPEN/uf_sket_types.h:76-80` 定义 0=UF_SKET_reference、1=UF_SKET_active；第4轮已核对实际调用，不只依赖 Builder.ConvertToReference=false 的参数。
