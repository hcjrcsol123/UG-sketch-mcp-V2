# 几何与工具离线检查

在项目根运行：

```powershell
$env:PYTHONUTF8 = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
python -B -m unittest discover -s tests/unit -p 'test_*.py'
```

覆盖单位换算、局部坐标、解析几何、F07—F14 oracle、安装 smoke helper、夹具解析及螺旋工具参数。部分 helper 序列化测试会在临时目录编译 C# 程序，需要本机编译器；这些检查不启动 NX，不等于实机或制造验收。

各阶段 smoke helper 的固定工具数量属于当时安装版本。当前源码的兼容性测试检查阶段工具集合包含关系和详细 schema；完整现行工具集合由 `tests/m2/test_mcp_stdio.py` 核对。历史检查数量不能替代当前能力发现。
