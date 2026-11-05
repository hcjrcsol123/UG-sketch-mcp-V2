# NX Sketch MCP 已有安装

此目录包含已配置的 NX Sketch MCP 安装。功能、连接方式与使用边界见 [使用说明](app/README.md) 和 [V2 功能索引](app/docs/V2.md)。当前软件版本为 0.22.0，服务提供 42 个工具；重新连接 MCP 客户端后加载当前工具 schema。

从关联的源码树更新前，正常保存并关闭 NX、Journal 和 helper，完成构建，然后运行 `pwsh -NoProfile -File tools/install-local.ps1 -Version 0.22.0`。更新脚本要求已有 `installation.json` 且 `source_root` 与源码树一致；它不执行首次安装或客户端注册。

换机需要本机获授权的 NX、许可环境、MCP 配置和指向本机绝对路径的专用启动 profile；首次安装和跨机流程尚未验证。UNKNOWN 时不重放、不自动解冻；不要输出令牌，也不要热替换已加载 DLL 或强制终止 NX。
