<div align="center">

# DSH 控制面板 🐋

**给 DeepSeek Harness 用的 Windows 托盘控制台。**

[English](README_EN.md) · [下载最新版](https://github.com/ee22ee/dsh-control-panel/releases/latest) · [反馈问题](https://github.com/ee22ee/dsh-control-panel/issues)

</div>

![界面预览](docs/preview.png)

功能：启停服务、打开网页、勾选插件、看日志、改端口。

- **界面是网页渲染的**：v0.2 起内页改用 WebView2（HTML/CSS/JS 全嵌在 exe 里，不落盘），配色、字体、动效都按现代控制台做。
- **绿色**：解压即用，无安装程序；配置跟着 exe 走，拷走就带着自己的设置。
- **托盘常驻**：收进托盘，左键唤出面板，右键直接启停。
- **高 DPI**：125% / 150% / 200% 支持缩放。

> v0.2 起需要 **Microsoft Edge WebView2 Runtime**：Windows 11 自带；Windows 10 如果装过新版 Edge 就已经有了，
> 否则从 <https://developer.microsoft.com/microsoft-edge/webview2/> 装一次 Evergreen Runtime。
> 没装也能启动——面板会提示原因，并自动回退到 v0.1 的自绘界面。

## 能做什么

| 页面 | 内容 |
| --- | --- |
| 服务 | 启动 / 停止 / 重启 DSH；无地址栏独立窗口打开 DSH；勾选启用哪些插件（改完下次启动生效）；实时显示端口、PID、监听状态 |
| 设置 | DSH 目录、开机自启、启动后自动开网页、启动后最小化到托盘、浏览器打开方式、关闭面板时是否停服、端口、启动纯净 DSH、开发者模式；整页可滚动 |
| 日志 | 实时刷新服务日志与面板日志，可清空、可折叠 |
| 关于 | 版本与链接 |

## 快速开始

### 直接用

1. 先装好 DeepSeek Harness。装好后 `%USERPROFILE%\.dsh\profiles\web` 应该存在。
2. 把 `DshPanel.exe`、`DeepSeekHarness-WhaleGirl.ico` 和 3 个 WebView2 dll
   （`Microsoft.Web.WebView2.Core.dll`、`Microsoft.Web.WebView2.WinForms.dll`、`WebView2Loader.dll`）
   **放在同一个文件夹**，双击 exe。
3. 第一次启动会自己去两个地方找 DSH 数据目录：
   - 环境变量 `DSH_HOME`
   - `%USERPROFILE%\.dsh`

   找到了直接可用。**没找到**会弹窗提醒，并自动跳到「设置 → DSH 目录」，点「选择…」指到你的 DSH 数据目录（里面应该有 `profiles` 文件夹）就行。
4. 点「启动并打开 DSH」，服务起来后浏览器会打开 `http://127.0.0.1:3080`。

注意：面板首次启动时**只找DSH安装时的默认位置**

### 从源码编译

需要 Windows 自带的 .NET Framework 4.x（`csc.exe` 在 `C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\`）。
WebView2 SDK 已经放在 `lib\` 里，不用另外装 NuGet。

```bat
build-panel.cmd
```

产物在 `dist\DshPanel.exe`，图标和 3 个 WebView2 dll 会自动拷到旁边。
界面资源在 `ui\`（`index.html` / `style.css` / `app.js` / `avatar.png`），编译时用 `/resource:` 嵌进 exe。

### 或者——

发给你的Agent，说：我要这个！

## 配置

配置写在 `panel.config.ini`：优先用 exe 旁边那份（绿色版，拷走就带着设置）；exe 所在目录不可写（比如放进 `Program Files`）时自动退到 `%LOCALAPPDATA%\DshPanel`。首次运行会生成一份带注释的默认配置。

| 键 | 说明 |
| --- | --- |
| `dshhome` | DSH 数据目录。留空 = 自动探测（先 `DSH_HOME`，再 `%USERPROFILE%\.dsh`） |
| `port` | 服务端口，默认 `3080` |
| `workdir` | 启动 DSH 时的工作目录，默认用户主目录 |
| `pluginjson` | 插件清单路径。留空 = 跟着 `dshhome` 走 `profiles\web\package.json` |
| `svccmd` | 启动脚本写到哪。留空 = 面板数据目录下的 `dsh-run.cmd` |
| `browser` | `app` = 无地址栏独立窗口；`tab` = 系统默认浏览器标签页 |
| `browserpath` / `browserprofile` | 指定浏览器 / 独立窗口用的 profile 目录，留空自动 |
| `autostart` / `autoopen` / `autotray` | 开机自启 / 启动后开网页 / 启动后最小化到托盘 |
| `stoponsvcclose` | 关闭面板时一并停止 DSH |
| `darktheme` | 深色主题，默认开 |
| `devmode` | 每 20 秒往日志写一次心跳，排查用 |

改完保存，重启面板生效。

## 常见问题

**提示「没有找到 DeepSeek Harness 的数据目录」**
面板只认默认位置。到「设置 → DSH 目录」点「选择…」，指到 `%USERPROFILE%\.dsh`；装到别处的话指到你自己的 `DSH_HOME`。

**启动失败：90 秒内未检测到端口监听**
失败时面板会把服务日志的最后几行贴进面板日志，照着看。最常见的是端口被别的程序占了，换个 `port` 再试。

**插件改完没生效**
开关是写进 `package.json` 的，要重启 DSH 才生效。服务运行中开关是锁住的，先停再改。

**更新 DSH 之后起不来了，是不是插件搞坏了**
「设置 → 高级 → 启动纯净 DSH」用官方默认插件集另起一个实例排查，不动你原来的配置。

**开机自启写在哪**
当前用户的启动项（`HKCU`），不触发 UAC；设置里随时能关。

## 项目结构

```
DshPanel.cs        主窗口、托盘、服务启停、配置读写、DPI 缩放
PanelUi.cs         外壳布局（标题栏/导航/旧内页）+ 数据推送到网页、把网页的点击派回业务逻辑
PanelHost.cs       WebView2 宿主：初始化、内嵌资源拦截、JS 双向通信
PanelTheme.cs      自绘控件与配色（v0.2 起只服务外壳层）
PanelPlugins.cs    插件清单读写
PanelExtra.cs      小工具
ui/index.html      界面本体：服务 / 设置 / 日志 / 关于 四页
ui/style.css       深色 / 浅色两套 CSS 变量
ui/app.js          收 C# 推来的 JSON 渲染，点击发 JSON 回去
lib/               WebView2 SDK（编译用，建包时拷到 exe 旁边）
app.manifest       高 DPI 声明
build-panel.cmd    一键编译 + 安装到 dist\
docs/preview.png   README 里的界面预览图
```

## 鸣谢

- 界面布局与配色参考了 [Steamcommunity 302](https://www.dogfight360.com/blog/18682/)（Dogfight360）的控制台式布局：左侧导航 + 右侧分栏卡片。
- README 的组织方式参考了 [QCYTSN/dsh-dafeiyu](https://github.com/QCYTSN/dsh-dafeiyu)。
- 角色图标来自 [deepseek-whale-girl-icon](https://github.com/) 素材（如涉及授权问题请联系作者处理）。
- 感谢 DeepSeek Harness 团队做出这么好用的 Agent 宿主。

## 许可

MIT
