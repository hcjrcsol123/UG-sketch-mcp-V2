# 测试与验证

在仓库根目录配置 Python 后，可运行不依赖本地历史证据或构建产物的离线子集：

```powershell
$env:PYTHONUTF8 = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
python -B -m unittest discover -s tests/unit -p test_local_frame.py
python -B -m unittest discover -s tests/unit -p test_unit_conversion.py
```

上述两个子集分别为 7 和 4 项，曾在本机通过；这些检查不等同于实际 NX 几何验证。全量 `tests/m2` 和 `tests/unit` 还依赖 C# 桥接构建或本地测试输入。当前不能声称全量回归通过：历史 `tests/m2` 记录为 355/356，`M3RectangleContractHarness` 仍反射已移除的 `ValidateRectangleCorners`；F21 inventory 测试依赖本机 EXE 和历史 JSON，干净环境可能缺少所需输入。测试失败或缺失依赖应保留并报告，不以删除或跳过掩盖。

真实 NX 定向测试需要 NX 2506、有效许可、已配置的桥接和经授权的独立测试零件；按具体测试要求串行执行。使用 `tests/nx` 或 `tools/smoke-*` 前应阅读参数和副作用，保存工作并核查所有权。连接准备检查 `tests/nx/smoke-readiness.py` 使用已安装配置与宿主路径，可检查启动和连接复用，不验证模型操作。独立 Python Journal 可产生文件、网络和模型副作用，其观察范围见 [F22](F22.md)。

F01—F22 曾在各自记录的限定域内接受，不能推断全部几何、NX 版本、操作组合或跨机安装已验证。当前启动器曾在本机完成中文界面启动、主线程响应与进程复用检查，没有模型调用；首次安装及跨机部署没有实测。数值门限与有限采样不构成制造精度或合规保证。
