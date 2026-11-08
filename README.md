<div align="center">

# DSH 控制面板 🐋

**DeepSeek Harness 的 Windows 启动器与托盘控制台。**

`v2.0` · 单渲染面重写版

</div>

---

## 这是什么

DeepSeek Harness（DSH）是一个本地 Web 服务。这个面板负责把它拉起来、停下来、打开网页、
开关插件、看日志——省掉每次手敲 `npx @deepseek-ai/dsh web --port 3080`。

## v2.0 是重写版

v0.2 的外壳是 WinForms 自绘（标题栏、导航、卡片全是 GDI+ 画的），内页是 WebView2，
两套渲染并存带来一串问题：主题切换半新半旧、滚动要装消息过滤器去截、切页靠截图做动画、
高 DPI 下三套坐标系互相打架。

v2.0 把外壳也搬进网页：**整个窗口只有一个 WebView2**，标题栏和导航由 HTML/CSS 绘制，
WinForms 退化成无边框窗口容器。结果是：

- 只有一条渲染路径、一套动画系统、一套坐标系
- 切主题 = 改一个 CSS 变量，原子生效
- 原生滚动、原生过渡
- 服务管理、配置、插件、浏览器、托盘逻辑全部重写，逐条规避 v0.2 踩过的坑

旧版本完整归档在 [`v1/`](v1/)——源码、文档、README 都在里面，随时可对照。

## 功能

| 页面 | 内容 |
|---|---|
| **服务** | 启动 / 停止 / 重启；无地址栏独立窗口打开 DSH；实时显示端口、PID、状态 |
| **插件** | 读写 `profiles/web/package.json` 的 `bundles` 数组，开关在下次启动生效；命脉插件锁定不可关 |
| **设置** | DSH 目录、开机自启、启动后自动开网页、关闭时收托盘 / 停服务、浏览器选择、端口、纯净 DSH、开发者模式 |
| **日志** | 实时服务日志，可清空 |

托盘：左键唤出面板，右键菜单可启停、重启、看日志、退出。

其他细节：

- **独立窗口**：`--app` 模式，无地址栏，带独立 `--user-data-dir`，不干扰日常浏览器
- **令牌地址**：等 DSH 真正打印出带令牌的 URL 才打开，避免落到 401 页面
- **插件清单是文本外科手术**：只替换 `bundles` 数组那一段，缩进、键序、注释都不动
- **单实例**：重复启动会唤醒已有窗口，不会开出第二个进程

## 系统要求

- Windows 10 / 11
- .NET Desktop Runtime 10（`Microsoft.WindowsDesktop.App`）
- Microsoft Edge WebView2 Runtime（Win11 自带；Win10 装过新版 Edge 就有了）

发布方式是 framework-dependent，exe 约 292 KB，不带运行时。

## 构建

```bash
./build.sh              # 编译 Debug
./build.sh release      # 发布到 dist/（framework-dependent）
./build.sh selftest     # 编译并跑内置自检
```

也可以直接用 dotnet：

```bash
dotnet build
dotnet publish -c Release -o dist
```

### 内置自检

不需要测试框架，纯逻辑都走程序内的断言：

```bash
DshPanel2.exe --selftest    # 结果写 selftest.log，退出码 0 = 全绿
```

覆盖 INI 解析与写回、插件 `bundles` 解析与保存防线、端口探活等。

## 配置

首次运行在 exe 同级生成 `panel.config.ini`（纯文本 `key = value`，`#` 或 `;` 开头是注释）。
设置页的改动会自动写回，手改也有效。删掉整个文件会重新生成默认配置。

配置文件位置按「绿色版优先」挑选：exe 同级 → 面板数据目录 → 当前目录。

## 目录

```
Program.cs            入口：DPI、单实例、异常兜底、日志
Config.cs             路径探测（带回退链）+ INI 解析与写回
ServiceController.cs  启停 / 三态端口探活 / 状态机去抖 / 日志增量
PluginStore.cs        package.json 的 bundles 文本外科手术
BrowserLauncher.cs    浏览器探测 / --app 独立窗口 / 令牌地址抓取 / 窗口检测
TrayManager.cs        托盘图标（三级降级）与菜单
WebHost.cs            WebView2 宿主 + 内嵌资源拦截 + 线程安全 Post
UiBridge.cs           C# ↔ 网页的消息协议
MainForm.cs           无边框窗口 + 首帧隐藏 + 错误页
AutoStart.cs          开机自启（HKCU）
SelfTest.cs           内置自检
ui/                   界面（HTML/CSS/JS，编译时内嵌进 exe，不落盘）
docs/                 设计记录与界面规格
scripts/              验证工具（截图、CDP 调试、快捷方式）
v1/                   旧版本归档
```

## 开发说明

调界面时可以开远程调试端口：

```bash
DSH_PANEL_DEBUG=1 ./dist/DshPanel2.exe
```

然后用 `scripts/cdp.mjs` 直接查 DOM、点按钮、抓页面像素：

```bash
node scripts/cdp.mjs dom                    # 量所有元素矩形
node scripts/cdp.mjs eval "innerWidth"      # 执行表达式
node scripts/cdp.mjs click "#btnStart"      # 点击
node scripts/cdp.mjs shot out.png           # 抓页面像素
```

比屏幕截图可靠：不受窗口位置、DPI 虚拟化、其它窗口遮挡影响。

## 许可

见 [LICENSE](LICENSE)。
