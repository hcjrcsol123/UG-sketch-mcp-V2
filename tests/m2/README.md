# MCP 功能回归

在项目根运行：

```powershell
$env:PYTHONUTF8 = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
python -B -m unittest discover -s tests/m2 -p 'test_*.py'
```

覆盖 STDIO 工具发现、参数校验、CLR 模拟适配器所有权/队列、F01—F14 协议与生命周期、计划、冻结、回读、保存和配置约束。`test_mcp_stdio.py` 维护现行完整工具集合；早期功能测试保留各自 schema 检查，不将旧版本的全局工具数作为当前约束。

需要本机 Python、PowerShell、C# 编译器；部分检查还需 `UGII_BASE_DIR` 和 `workspace/build/current/M2/` 当前桥程序集及 NXOpen 托管引用。`build_harness.ps1` 和测试内编译器在临时目录构建离线 CLR 程序，不启动 NX。缺少环境时产生的 skip 不应计作通过。UTF-8 环境用于正确读取中文 CLR 诊断，禁用字节码避免重新生成缓存。

`tests/nx/` 和 `tools/smoke-*` 为另需授权的实机入口，使用独立会话/零件。能力与安全边界见[项目说明](../../README.md)，运行前置及已知回归缺口见[测试说明](../../docs/testing.md)。
