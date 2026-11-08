using System.Reflection;

// exe 的文件属性里显示的版本信息。csc 没有 /win32version 参数，版本只能从这里来。
// 界面上的版本号在 PanelUi.VerText（外壳）和 ui/index.html 的 ver-line（关于页），改版本时三处一起改。
[assembly: AssemblyTitle("DeepSeek Harness 控制台")]
[assembly: AssemblyProduct("DeepSeek Harness 控制台")]
[assembly: AssemblyDescription("给 DeepSeek Harness 套一个托盘外壳：一键启停服务、图形化开关插件")]
[assembly: AssemblyCompany("by deepseek 和大米饭")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
