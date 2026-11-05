# F20 API 与接口记录

未测边界不当作实测支持。

## 设计约定

- 世界坐标 mm，角度 deg；独立原生曲线，不写入 Sketch。
- 圆心定义显式法向和面内 x_axis，零角沿 x_axis，角度按右手系。
- 三点弧的第二点是经过点；必须选择包含它的有向弧段。
- 整圆显式选择 circle，不靠零扫角猜测整圆。
- 共用当前所有权／binding epoch／revision；同 ID 结果查询不代表允许重放 UNKNOWN。

## 接口

`nx_prepare_spatial_curve` 共同必填 `operation_id`、`ownership_id`、`binding_epoch`、`revision_id`、`curve_type`。以下字段组严格互斥，额外字段拒绝：

| curve_type／definition_mode | 附加必填字段 |
| --- | --- |
| `line`（不传 definition_mode） | `start_mm`、`end_mm` |
| `arc`／`center` | `center_mm`、`normal`、`x_axis`、`radius_mm`、`start_angle_deg`、`end_angle_deg` |
| `arc`／`three_points` | `start_mm`、`through_mm`、`end_mm` |
| `circle`（不传 definition_mode） | `center_mm`、`normal`、`x_axis`、`radius_mm` |

数值域：每个输入坐标绝对值≤1e9 mm，半径在 (1e-6,1e9] mm；线端点、三点的任意两点距离>1e-6 mm。法向与 x_axis 长度距1误差≤1e-9，两者点积绝对值≤1e-9，不静默修正。中心弧角度各在±1e9 deg内，signed sweep=end−start，1e-7 deg<abs(sweep)<360 deg。三点还需叉积模长>1e-8×两条基向量长度乘积，推导圆心及半径也须满足上述数值域，推导扫角>1e-7 deg且经过中间点；产品后验不能证明者拒绝。输入接受域不等于数值极限已实测。

`x_axis` 为零角方向，`y_axis=normal×x_axis`；正角按此右手框架前进。三点圆弧从 start 出发经过 through 到 end，可为优弧；整圆从 x_axis 出发按 normal 正向走一周。负扫角使用反向原生法向与正原生角跨度表示，保持世界行进点序；`native_direction_mapping` 显式记录转换。

独立曲线不等同于 NX Feature：首版使用 `Part.Curves.CreateLine/CreateArc`；输出原生 Line／Arc JID。F20 prepare 对 active Sketch 保守返回 NX_BUSY，要求先结束草图。对象标识仅使用当前会话／修订读取的值，不假定跨重开 Tag 不变。

## 原生读回与限制

原生 `Part.Curves.CreateLine/CreateArc` 不创建独立 Feature。Arc回读真实frame、半径、起止弧度与长度，并计算实际世界端点和中点；三点经过点由真实frame及经过参数求得，原输入单列 requested_through_mm。完整五点UF求值属于独立验收证据，不误称生产每次后验都进行了UF五点读取。

`geometry` 和 `model_preservation` 返回实际判据，旧曲线身份变更另列映射。既有Point／Vector沿用数字字符串数组，scalar radius／angle／length为JSON数值，消费者不得据此猜跨会话Tag稳定。动态JID和SaveCopy完整类型／几何一对一映射及其容差见[F20](F20.md)。

Python/C#拒绝模式混用、缺参、非有限及bool-as-number；活动草图NX_BUSY，修订变化STALE_REVISION／STALE_PLAN。真实提交失败验证恢复后为M3_OPERATION_ROLLED_BACK；无法证实恢复／清理则UNKNOWN并停写，不自动解冻。未实测live断链或失败Undo、极端尺度、全部混合旧特征、全部方向／角度或其他NX版本。
