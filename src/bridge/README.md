# NX .NET 桥接

此目录包含 NX2506／.NET Framework 本机桥接与原生建模实现。当前 MCP 软件版本为 0.22.0，工具定义位于 Python 服务，共42项。功能范围见 [V2](../../docs/V2.md)，验证条件和已知缺口见[测试说明](../../docs/testing.md)。

- `M2Bridge.cs` 和契约维护 IPC、所有权、主线程队列、修订、计划与操作生命周期；`M2JsonlHelper.cs` 提供 JSONL 客户端，`M2Entry.cs` 接入 NXOpen adapter。
- `M2OpenModelAdapter.cs` 负责模型读取、建模分派、验证与保存重开；各 `V2*Factory.cs`／`V2*Adapter.cs` 实现限定范围的草图、实体、空间曲线和扫掠操作。
- `M3NxFeatureFactory.cs` 保留基础草图与拉伸路径；`M7NxHelicalFactory.cs` 实现既有实体螺纹和螺旋槽。具体参数及支持范围以功能文档为准。
- `M2Startup.cs` 通过 NX Startup 入口加载已配置的 Bridge／Entry，只建立桥接，不接管或读取模型。独立 Python Journal 的执行与协调逻辑在 Python 服务中，不是本目录的通用 C# Journal 能力。

在项目根运行 `tools/build-m2-bridge.ps1`，输出位于 `workspace/build/current/M2/`。构建依赖本机 NXOpen 程序集；供应商程序集和本机构建产物不随源码分发。替换已加载 DLL 前须正常退出相关 NX 会话，禁止热替换或强杀。

单位为毫米，公共角度字段以各工具契约为准。修改后重新查询对象标识；Tag 不跨会话复用。预览不得遗留临时对象，无法证明回滚或结果时进入 UNKNOWN 并停止后续写入，不自动重放或解冻。

C# 包围盒接口为 `UFSession.Modl.AskBoundingBox(tag, double[])`；圆弧半径与角度采用 double。Python 与 C# 的 NXOpen 命名及重载不同，不能直接互换。运行和本地配置见[项目说明](../../README.md)。
