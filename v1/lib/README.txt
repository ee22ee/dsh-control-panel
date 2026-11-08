WebView2 SDK（Microsoft.Web.WebView2 1.0.4258.31，NuGet 包 lib\net462 + runtimes\win-x64\native）

随仓库提交这三个文件，编译脚本直接引用，使用者无需装 NuGet 或 SDK：

  Microsoft.Web.WebView2.Core.dll      698,208 字节
  Microsoft.Web.WebView2.WinForms.dll   38,752 字节
  WebView2Loader.dll                   194,912 字节

运行时要求：Windows 10/11 上装有 WebView2 Runtime。
Win11 自带；Win10 一般随 Edge 一起装过。缺失时面板会提示下载地址。
下载：https://developer.microsoft.com/microsoft-edge/webview2/
