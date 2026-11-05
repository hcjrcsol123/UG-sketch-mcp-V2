# F18 API 与原生读回札记

本文记录接口和 NX2506 实测探索，供复核使用；探针不是产品或安装验收。r05 首轮 installed 失败；r06 第二轮 installed 和只读重开已记录通过。

## 请求合同

`nx_prepare_feature_pattern` 路由到 `operation.prepare_feature_pattern`。共同必需：`operation_id`、`ownership_id`、`binding_epoch`、`revision_id`、`seed_feature_id`、`mode`、`count1`。

| mode | 附加必需字段 |
| --- | --- |
| `linear` | `direction1`, `pitch1_mm` |
| `rectangular` | `direction1`, `pitch1_mm`, `direction2`, `pitch2_mm`, `count2` |
| `circular` | `axis_origin_mm`, `axis_direction`, `angle_deg` |

不同模式字段互斥，额外字段拒绝。各 count 是 2–32 的整数且拒绝布尔值，矩形乘积至多 32；数量包括种子。方向三个分量必须有限、为单位向量且长度误差至多 `1e-9`；矩形方向点积绝对值至多 `1e-9`。间距为正有限数，轴点为三个有限数字。圆周 `angle_deg` 为有符号非零有限步长，绕给定轴遵循右手规则，`(count1-1)*abs(angle_deg)<360`，不含等号。长度用 mm，角度用度。世界轴方向平移与旋转公式见 [F18 主文](F18.md)。

作用域限完整轴对齐单实体 `BLOCK`/`EXTRUDE` Create 种子，且无下游修改；变换后 AABB 与自身其他实例及其他现存实体必须分离。预期变换坐标超出 `1e8 mm` 时属于保守几何范围外，由 prepare 拒绝；输入合法不保证几何可执行。活动绑定、当前修订和能力门控仍然有效。

原始 host stderr 未持久化，未通过服务端 `operation.get` 查询确认。

## 探针观察

Pattern Feature 的 `GetBodies` 可为空，不能把它当成实例实体集：需从原生实例层级、输出特征、实体与预期变换逐一核对。

不能只靠 Destroy 或恢复 dirty 标志证明读操作无副作用。

内层 Undo 应物化数据、销毁 builder、恢复并复核库存和 dirty 状态；任何清理或恢复不能证明时进入 UNKNOWN。

该 API 探针在将拉伸种子高度从 4 改为 5 mm 时观察到实例几何随之更新；这只是一项探针观察，尚未将种子编辑后的传播作为产品行为单独验收。

产品必须严格核验新对象的所属容器、表达式 owner 和引用，不能因对象类型或名字相似就认定可安全保留。所有探针结论仅适用于所记录运行，不证明其他 NX 版本或一般模型。

每例 fresh 及 execute/fresh 几何 oracle、已有 Pattern 保全、成功同 ID 复用、SaveCopy 和 HUMAN_OWNED 归还均在报告内；这些是固定样本而非广泛可靠性数据。

## 身份与验收边界

r02 矩形失败排查中，既有 point 的同会话 Tag 和坐标保持，但 `ENTITY` JournalIdentifier 发生重排；不能把不稳定编号当成实体实变。r03 针对性修复：同会话核对 Pattern 的 ReferencePoint 引用、Tag 与坐标；跨会话依据坐标和 inferred 状态核对，不比较 Tag。正式 SaveCopy 的只读重开及独立离线审计已记录坐标与 inferred 结果。

原生结果和 fresh 应记录原生定义、seed、各实例及完整顶点、边端点、面边邻接、保全状态。独立 oracle 复算实际 `vertices_mm`、边和面邻接，并逐一匹配实例；对未标记盒体的完全对称旋转无法单凭几何区分，须结合原生方向/轴定义和关联。证据模型、JSON 记录及记录范围见 [F18 主文](F18.md)。

原生圆周轴 `[110,0,0]` mm、`-70°` 与安装版轴 `[300,0,0]` mm、`-90°` 是不同样本。实测种子是 EXTRUDE；BLOCK sentinel 不等于 BLOCK 种子建模验证。
