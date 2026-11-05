# F19 原生 API 与读回记录

r16 两项唯一原生建模样本及其 fresh、installed 两项唯一镜像建模和产品 SaveCopy/reopen、独立只读重开已通过。

`nx_prepare_feature_mirror` 路由到 `operation.prepare_feature_mirror`。共同必需字段是 `operation_id`、`ownership_id`、`binding_epoch`、`revision_id` 和 `seed_feature_id`；以下两种平面定义严格二选一：

- `plane_origin_mm` 与 `plane_normal`：各三个有限数字，法向长度与 1 的差绝对值不超过 `1e-9`，不自动归一化。
- `datum_plane_id`：当前工作部件的原生 `DatumPlaneFeature` JournalIdentifier；不是跨会话 Tag。

多余字段、半组平面、混用模式均拒绝。几何使用 mm。当前保守实现要求无下游修改的 Create 模式 `BLOCK` 或 `EXTRUDE`，其实体为完整轴对齐 box；反射结果与原体及其他实体的轴对齐包围盒须有大于 `1e-6 mm` 的间隙，变换顶点坐标绝对值不大于 `1e8 mm`。支持域外的模型应拒绝，不能由此推断复杂特征、相交体或其他 NX 版本的支持。

NX2506 原生接口探索观察：`MirrorFeatureBuilder.FeatureSet` 指定种子；数值平面使用 `PlaneOption.New` 与 `PlaneConstructor`，datum 使用 `PlaneOption.Existing` 与 `Plane.Value` 指向原生 DatumPlane。`CommitFeature` 返回 `MirrorFeature`，`FeatureType` 为 `MIRROR_SET`；其 `GetBodies` 返回输出实体，探针中另一 BLOCK feature 的 `GetBodies` 与其共享实体，而 `ContainerFeature` 为 null，不应将该输出声称为容器子特征。`FeatureSet.GetArray()`、平面 origin/normal、datum feature 与原生父关系用于读回。

上述探针的数值平面 `Method` 为 `0`；离线反射确认这是 `Undefined`，`Fixed` 的值为 `38`。r06 的窄修仅在 `PlaneOption.New` 且 `GetGeometry()` 为空的数值模式接受 `Undefined` 或 `Fixed`，仍验证 origin、normal、种子、父关联与唯一输出；已有 datum 使用独立 `Existing` 引用路径。

r03 datum stale 是独立的另一通过控制。种子 EXTRUDE 表达式可能被输出 EXTRUDE 共用，产品仅允许已证实的特定 owner/users 变化并记录前后原始信息，不声称表达式所有权完全不变。

读回隔离在内层 Undo 中，前后核对 dirty 和库存；清理或恢复无法证明时必须按 UNKNOWN 处理，不重放或自动解冻。该 dirty 探针添加的点未出现在可枚举 Points 集合，不能据此声称已证明保全可枚举调用者编辑或完整嵌套 Undo。正式控制须另给证据。

镜像预期由 `q = p - 2 n dot(p-o,n)` 计算，`n` 为单位法向；反射改变手性，面外法向须由面相对实体内部定向。实现使用面中心的 `AskFaceProps` unit normal，而非将 `AskFaceData` 的 `normal_sense` 直接乘以 direction；还核对顶点、直边、裁剪面、双向邻接、面积和体积。探索不是产品完整性或持久化验收。
