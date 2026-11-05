# F22 API 核查（本机 NX）

随附 `DotNet/ValidateNXOpenSetup/ValidateNXOpenSetup_Readme.txt` 记载 `run_journal ValidateNXOpenSetup.cs`。本机先后对两个独立 C# 脚本调用入口：均退出 0，但未产生 Console 内容或第二个脚本请求写出的文件，不能确认脚本得到执行。因而不开放 C#，也不把这个退出码计作成功。此结论仅针对本机探针，并非宣称 NX 一般不支持 C#。
