
# F13 NXOpen API 核对

探针与正式产品案例分开记录；不以 API 存在、编译通过或单一体积比较冒称实际几何验证。

生产公开入口是 `nx_prepare_revolve`，然后由 `nx_execute_plan` 提交，最后必须显式调用 `nx_verify_part` 做 fresh 原生读回；`nx_get_operation` 对同一 operation ID 只查结果，不重放。

`nx_prepare_revolve` 必须提供 operation/ownership/binding epoch/revision、显式 `sketch_id`、模式、世界坐标轴点与方向、起止角。`profile_output` 可省略并默认为 `solid`；`target_body_id` 仅布尔模式必填。轴点为 3 个有限数且每轴坐标在 ±1,000,000 mm 内；方向为 3 个有限数且非零，使用抗溢出的缩放归一化；端点各在 [-360°, 360°]，有符号跨度绝对值大于 1e-9°且不超过 360°。轴线须位于草图平面内。`create`、`unite`、`subtract` 输出 solid；`sheet` 仅 `create`。负跨度转换为反向原生轴和取负角端点，保留等价的有向旋转语义。

截面来自指定草图的**全部有效非 reference 曲线**，NX 原生 Section 输出域必须与预分析域一致。solid 需要有效闭合区域，sheet 允许开放链且仍须通过原生 builder 校验。

执行返回完整 `body_ids` 与 `bodies`。布尔目标要求一个明确的当前 solid 目标；目标 Tag 由执行前实体快照解析并与 native builder 目标核对，JournalIdentifier 仅作为诊断关联。未选实体按其直接记录作非目标保全核对。Rollback 只有在 NX Undo、零更新错误及实际状态恢复均被证明时报告成功，并推进 revision；同 ID 查询不再执行。

### 面引用和持久化

`nx_query_faces` 的 `face_ids` 是可重复 JID 的有序多重集，顺序按 JID、Tag 排列；`face_refs` 与其逐项对应，新增 `face_tag` 供同体同 JID 多面的会话内区分。受支持的 Sketch/Datum 面支撑请求可选带 `support_face_tag`，但提供时必须同时有 `support_face_id`。JID 唯一时省略 Tag 的旧请求继续有效；一个 Body 上相同 JID 对应多个 Face 时，省略 Tag 返回 `SUPPORT_FACE_AMBIGUOUS`，给出错误 Tag 返回 `SUPPORT_FACE_NOT_FOUND`。Tag 只对当前 NX 会话及修订有效，不是持久身份。

SaveCopy 对无法唯一解析的面关联或未知关联，在触碰目标文件前拒绝；重开后才发现关联不匹配的情形保留实际文件并报告 `SAVED` 与 `associationMismatch`。含重复 Face JID 时，固定/未知 Datum 来源继续保守拒绝；有证据的 `WORLD_FRAME` 和 `two_lines` 非面支撑可保存，不因无 Face Tag 而误判。该范围不承诺未知 Datum 的一般持久化兼容。

### 数值结果解释

MCP 通用 inspect 的质量属性 accuracy 为 `0.99`；F13 execute/fresh 的直接体积读回使用 `0.999999`。这些是 NX API 调用设置，不是精度保证。r16 与 r21 的 N04 椭圆均未声称 NX mass-property 精度提高；原 mass-property 值保留为近似诊断。仅当单 solid、单 face、零 edge、拓扑类型 5、完整双周期/seam/法向与内外 witness 检查，且六组 32/64/128 网格、两 offset、两 origin 收敛门槛全通过时，才使用独立 signed surface divergence integral；r16 原值 `6316.5468166971905 mm³` 对 `640π² mm³` 在原限内。此有限采样既非严格误差上界，也非独立物理测量或通用 fallback，不能推广到其它曲面。Circle Arc 仍走原 mass-property gate；Spline 在 F13 原生矩阵未新增代表性案例。

公开 `result_geometry` 的执行/fresh 比较只见两处圆柱面的 UV limit 差；完整 fingerprint 还含 surface sample 的 UV、point 与 normal 数值差。

r21 只在 fresh Verify 对受影响 F13 result body 的下列 face 路径使用容许机器舍入的比较。令 `ε=2⁻⁵²`：

- 对 `faces[*].uv_limits` 与 `faces[*].surface_samples[*].uv` 每个有限标量 `a,b`，要求 `|a-b| ≤ 64·ε·max(1,|a|,|b|)`。
- 对对应的 3D `point_mm`，要求欧氏差 `||p-q||₂ ≤ min(64·ε·S, 1e-7 mm)`；`S=max(1,两端六个坐标的最大绝对值)`。
- 对单位法向 `normal`，每个分量差 `≤64·ε`，两端须满足单位法向检查，且角差 `atan2(||n₁×n₂||, n₁·n₂) ≤ 1e-12 rad`。

仅上述 `face.uv_limits` 和 `surface_samples.uv/point_mm/normal` 路径适用；Tag、拓扑、CLR shape、bbox、radius、axis、edge、NURBS、其它 face/body 数值和其它几何字段仍精确。此规则只用于受影响 body 的 F13 fresh 比较；非目标、preflight、execute、产品 Undo、通用/F12 shared comparator 均不变。不得按字段名称对别的对象、其它曲面类型或任何实测偏差套用此规则。

为保留诊断，比较前后 raw 始终保留。r21 诊断容器按实际 UTF-8 编码总量上限 512 KiB；run4 integration 控制中两份记录合计 `502098 / 524288 bytes`，完整保存，未超预算。超预算须显式记录截断/遗漏，不能把截断当完整。法向或派生差值在极值下溢出时安全拒绝，失败 metric 用 `null` 且 `finite=false`，原始有限 token 留存，保证失败证据可由 `M2Json` 序列化。

这些不是 NX/MCP 案例，也不加到 NX 案例数。

安装 smoke 覆盖 partial solid、sheet、unite、schema/只读拒绝、Face Tag/refs、同 ID get 不重放、SaveCopy/reopen 与安全交还；未单独覆盖 installed subtract，subtract 在 native N07 覆盖。run1–run6的FAIL原样保留，不因run7通过而升级。

第二层审查发现 unite before/after canonical raw 字符数分别为195024与195143，诊断预算导致截断且57条accepted detail全部省略。因此不宣称逐路径独立检查了这57条。按 `ACCEPT_RECORDED_SCOPE`，unite 的记录范围依据为完整公开 geometry 字段仅一个UV limit差 `1.12757e-17`，并结合解析体积/包围盒、source/target/non-target证据、编译控制和同候选 native N06；截断限制明确保留。Face Tag 的 installed 记录是只读 inventory/query；带 Tag 的实际Sketch执行与SaveCopy写前拒绝由 native G01支持，Tag仍是session/revision身份，保存关联继续按已记录边界保守拒绝。

负责人已接受这一记录范围。

## 原生构造探针

root 串行运行 `tools/v2/f13-api-probe.py`，使用独立临时毫米零件及 CurveDumb Section，不加载产品桥。以下均为 API 探针，不计产品验收案例。

不据此声明 NX 内核缺陷。

前两次启动沿用旧 F12 runner，因此外层 batch 文件名前缀为 f12；实际 Journal 路径及内部 scope 均明确 F13。随后使用 F13 runner。所有进程自行返回，无强制结束。

## 本机观察与实现决策

使用 `Features.CreateRevolveBuilder(null)`、原生 Section、Limits Start/End、FeatureOptions.BodyType、BooleanOperation、Offset.NoOffset。轴采用 `Points.CreatePoint` + `Directions.CreateDirection` + `Axes.CreateAxis(Point,Direction,WithinModeling)`，不采用本机提交失败的坐标/向量 overload。

本机原生特征类型为 `SWP104`。`Axis.Point` 返回 NXOpen.Point（坐标从 Coordinates 读），`Axis.Direction` 返回 NXOpen.Direction（从 Vector 读）。角度 Expression.Value 在本探针直接返回度。提交后轴/点/方向仍可经特征 builder 读出，Destroy 后重新创建 builder 的 Tag 与角度一致；公开 Part.Points/Axes/Directions 集合均为空，不能要求内部 helper 出现在这些集合中。

CurveDumb 探针 GetParents 为空、GetExpressions 只有起止两个表达式，不能据此外推产品 Sketch Section 父关联。负跨度原生提交成功不等于几何右手语义已验证；产品计划将负跨度转为反轴与取负角端点的等价正跨度，并以实际几何验收。

该脚本旧 passed 标志过早置 true，故该报告虽然有 true 也**不能宣称整轮通过**。该追加报告不替代前次完整定义回读探针，也不构成产品验收。
