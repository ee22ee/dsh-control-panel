<div align="center">

# DSH Control Panel 🐋

**A Windows tray console for DeepSeek Harness.**

[中文](README.md) · [Download](https://github.com/ee22ee/dsh-control-panel/releases/latest) · [Issues](https://github.com/ee22ee/dsh-control-panel/issues)

</div>

![Preview](docs/preview.png)

One `exe`, one icon and three WebView2 dlls — double-click and it runs. No terminal, no long `npx` incantations, no digging through `package.json` just to turn a plugin on. Start and stop the service, open the web UI, toggle plugins, tail logs and change the port, all from one window.

- **The pages are rendered as HTML**: since v0.2 the inner pages run in WebView2 (HTML/CSS/JS are embedded inside the exe, nothing is written to disk).
- **Portable**: unzip and run, no installer; the config travels with the exe.
- **Lives in the tray**: closing the window keeps the service running. Left-click the tray icon to bring the panel back, right-click for start/stop/restart/quit.
- **Genuinely HiDPI**: at 125% / 150% / 200% scaling the text, rounded corners and strokes stay crisp.
- **Leaves your DSH alone**: `package.json` is backed up before any plugin change; everything the panel writes stays in your own folders.

> v0.2 needs the **Microsoft Edge WebView2 Runtime**: bundled with Windows 11; on Windows 10 it is already present if you have a recent Edge,
> otherwise install the Evergreen Runtime once from <https://developer.microsoft.com/microsoft-edge/webview2/>.
> Without it the panel still starts — it explains why and falls back to the v0.1 self-drawn UI.

## What it does

| Page | Contents |
| --- | --- |
| Service | Start / stop / restart DSH; open DSH in a chrome-less app window; toggle enabled plugins (applies on next start); live port, PID and listening state |
| Settings | DSH directory, launch at login, open the browser after start, start minimized to tray, browser mode, stop the service with the panel, port, start a clean DSH instance, developer mode — the whole page scrolls |
| Logs | Live service log and panel log, clearable, collapsible |
| About | Version and links |

While restarting, the restart button spins; while the service runs, the plugin switches lock themselves so you cannot corrupt a half-written config.

## Quick start

### Use it

1. Install DeepSeek Harness first. Afterwards `%USERPROFILE%\.dsh\profiles\web` should exist.
2. Put `DshPanel.exe`, `DeepSeekHarness-WhaleGirl.ico` and the three WebView2 dlls
   (`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll`, `WebView2Loader.dll`)
   **in the same folder** and double-click the exe.
3. On first launch the panel probes two places for your DSH data directory:
   - the `DSH_HOME` environment variable
   - `%USERPROFILE%\.dsh`

   If found, you are good to go. If **not** found, it tells you and jumps to *Settings → DSH directory*; click *Choose…* and point it at your DSH data directory (the one containing a `profiles` folder).
4. Click *Start and open DSH*; the browser opens `http://127.0.0.1:3080`.

The panel only probes the **default install locations** — it never crawls the whole disk (slow, and easy to get wrong).

### Build from source

You only need the .NET Framework 4.x that ships with Windows (`csc.exe` under `C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\`). No Visual Studio, no NuGet: the WebView2 SDK ships in `lib\`.

```bat
build-panel.cmd
```

The output is `dist\DshPanel.exe`, with the icon and the three WebView2 dlls copied next to it.
The UI sources live in `ui\` (`index.html` / `style.css` / `app.js` / `avatar.png`) and are embedded into the exe with `/resource:`.

## Configuration

Settings live in `panel.config.ini`: the copy next to the exe wins (portable — move the exe and your settings come along). If that folder is not writable (e.g. `Program Files`), the panel falls back to `%LOCALAPPDATA%\DshPanel`. A commented default file is generated on first run.

| Key | Meaning |
| --- | --- |
| `dshhome` | DSH data directory. Empty = auto-probe (`DSH_HOME`, then `%USERPROFILE%\.dsh`) |
| `port` | Service port, default `3080` |
| `workdir` | Working directory used when launching DSH; defaults to your home |
| `pluginjson` | Plugin manifest path. Empty = `<dshhome>\profiles\web\package.json` |
| `svccmd` | Where the generated launch script goes |
| `browser` | `app` = chrome-less window, `tab` = default browser tab |
| `autostart` / `autoopen` / `autotray` | Launch at login / open browser after start / start minimized |
| `stoponsvcclose` | Stop DSH when the panel is closed |
| `darktheme` | Dark theme, on by default |
| `devmode` | Append a heartbeat to the log every 20 s |

## Troubleshooting

**"No DeepSeek Harness data directory found"** — point *Settings → DSH directory* at `%USERPROFILE%\.dsh`, or at your custom `DSH_HOME`.

**"Start failed: port not listening within 90 s"** — the panel copies the tail of the service log into its own log. Most often the port is taken; try another `port`.

**Plugin changes do nothing** — toggles are written to `package.json` and apply on the next DSH start. Switches are locked while the service is running.

**DSH broke after an update** — *Settings → Advanced → Start clean DSH* launches an instance with the official default plugin set, leaving your config untouched.

## Project layout

```
DshPanel.cs        main window, tray, service control, config, DPI scaling
PanelUi.cs         shell layout (title bar / nav / legacy pages) + pushes data to the page, routes clicks back
PanelHost.cs       WebView2 host: init, embedded-resource interception, JS bridge
PanelTheme.cs      owner-drawn widgets and palette (shell layer only since v0.2)
PanelPlugins.cs    plugin manifest read/write
PanelExtra.cs      small helpers
ui/index.html      the UI itself: service / settings / logs / about
ui/style.css       dark and light CSS variable sets
ui/app.js          renders the JSON pushed from C#, posts clicks back
lib/               WebView2 SDK (build-time; copied next to the exe)
app.manifest       HiDPI declaration
build-panel.cmd    one-click build + install into dist\
docs/preview.png   preview image used by this README
```

## Credits

- The control-panel layout (left nav + right card columns) and palette follow [Steamcommunity 302](https://www.dogfight360.com/blog/18682/) by Dogfight360.
- README structure inspired by [QCYTSN/dsh-dafeiyu](https://github.com/QCYTSN/dsh-dafeiyu).
- Whale-girl icon from the `deepseek-whale-girl-icon` asset set — contact the author for licensing questions.
- Thanks to the DeepSeek Harness team for a genuinely pleasant agent host.

## License

MIT
