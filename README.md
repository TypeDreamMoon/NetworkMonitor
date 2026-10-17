# NetworkMonitor

Windows 网速与流量监控，常驻托盘 / 悬浮窗 / 任务栏。

![screenshot](docs/screenshot.png)

## 功能

- 实时下载 / 上传速率，1 秒采样 + EMA 平滑，附最近 120 秒曲线
- **今日 / 本周 / 本月 / 总计** 流量，上下行分开统计；7 天 / 30 天柱状图
- **应用流量（FlClash / Clash Verge Rev）**：独立页面 + 首页简要卡片（Top 应用 / Top 节点），按应用 + 节点统计实际/计费流量，支持今日/本周/本月、分类饼图与节点倍率
- **独立设置页**：显示、系统、刷新速度、网卡、控制器、版本更新集中在一页
- **刷新速度**：1 / 2 / 5 秒可选，作用于网速、应用流量和界面刷新
- 系统托盘常驻：左键打开主界面，右键菜单切换悬浮窗 / 任务栏条 / 开机自启
- 桌面悬浮小窗：可拖动，双击打开主界面
- 任务栏网速条：贴在通知区域左侧，亚克力底
- 亚克力背景 + 圆角卡片，设置面板用 CommunityToolkit 的 `SettingsCard`
- 开机自启（`HKCU\...\Run`，不需要管理员权限）

## 应用流量（FlClash / Clash Verge Rev）

![traffic](docs/traffic.png)

节点按两列卡片展示，右上角齿轮可设置倍率：

![traffic nodes](docs/traffic-nodes.png)

> 截图为本机实际数据（FlClash TUN 模式，包含 DIRECT 规则）。

首页保留一个简要的 Top 应用卡片；点「查看全部」或标题栏 `▤` 进入独立的「应用流量」页，页面左上角的「← 首页」返回。

数据来自 Clash/Mihomo 的 **external controller**：

1. FlClash → 设置 → 常规 → 打开 **External controller**（默认 `127.0.0.1:9090`）
   Clash Verge Rev → 设置 → 打开 **External controller**（默认 `127.0.0.1:9097`）
2. 程序会自动读取 FlClash 的 `config.yaml` / `shared_preferences.json`，以及 Clash Verge Rev 的 `config.yaml` / `clash-verge.yaml`，再探测默认端口 9090 / 9097 / 9679，逐个验证、跳过残留的失效配置
3. 标题栏点 `▤`，或启动参数 `--traffic`，打开应用流量页

统计口径：

- 每秒轮询 `/connections`，按连接累计字节的增量归到「进程 + `chains[0]` 节点」
- 进程会映射成友好名称和分类（浏览器 / 开发 / 游戏 / 社交 / 媒体 / 下载 / AI / 系统 / 其他），列表按分类分组
- 应用流量页顶部有分类占比环形图，颜色和分类图标一致
- **节点倍率**：只在「应用流量」页的节点卡右上角齿轮设置（0–100，支持 1x/1.5x/2x/3x/5x）；首页节点摘要只读显示，不提供设置入口。界面同时显示「实际流量」和「计费流量 = 实际 × 倍率」，应用行和汇总也会带计费值。倍率只作用于展示/汇总，数据库里始终存实际字节，改倍率会立即重算历史
- `chains[0]` 就是实际出口节点；`DIRECT` 规则也会记为 `DIRECT`
- 数据按分钟写入 SQLite 的 `proxy_usage` 表，今日/本周/本月从台账聚合
- **只统计经过 FlClash 的连接**。完全不经过代理的直连流量不会出现在这里；后续可以用 ETW `Microsoft-Windows-Kernel-Network` 补全系统级按进程统计

## 数据口径

- 计数器来自 `GetIfTable2` 的 `MIB_IF_ROW2`（**64 位** `InOctets` / `OutOctets`），不是 32 位的 `GetIPv4Statistics`
- 每秒采样，按分钟写入 SQLite；`今日/本周/本月` 按本地时区聚合（周一为周首日）
- 默认统计所有 Up 的**物理、非隧道**网卡，排除 VPN / 回环 / WFP 过滤层，避免双重计数；可在界面里手动指定网卡
- 已处理：网卡禁用重启用导致计数器归零（丢弃该区间）、休眠唤醒时间跳变、Wi-Fi/有线切换
- **总计从第一次运行开始累计**，不是 Windows「数据使用量」的历史数据

## 技术栈

| 层 | 选型 |
|---|---|
| UI | .NET 10 + WinUI 3 / Windows App SDK 1.8（非打包） |
| 控件 | [CommunityToolkit.WinUI.Controls](https://github.com/CommunityToolkit/Windows)（Segmented / SettingsCard） |
| 托盘 | [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) |
| 存储 | Microsoft.Data.Sqlite（WAL 模式，分钟粒度台账） |
| 图标 | `tools/make_icon.py` 程序化生成多尺寸 ico |

## 界面实现要点

- 主窗口保留 DWM 边框/圆角/阴影，但去掉系统标题栏（`SetBorderAndTitleBar(true, false)`），最小化/关闭用自绘按钮；最大化被彻底禁用（移除 `WS_MAXIMIZEBOX` + 拦 `WM_GETMINMAXINFO` / `SC_MAXIMIZE` / 标题栏双击），避免最大化后自定义标题栏消失
- 悬浮窗、任务栏条是**无边框窗口**。WinUI 的 `SetBorderAndTitleBar(false, false)` 会残留 `WS_DLGFRAME`，在深色桌面上表现为一圈白边；启动后清掉 `WS_CAPTION | WS_DLGFRAME | WS_THICKFRAME | WS_BORDER | WS_SYSMENU` 等 frame 位并发送 `SWP_FRAMECHANGED`，白边即消失
- **任务栏条优先停靠**：`SetParent(Shell_TrayWnd)` + `WS_CHILD`，位置相对任务栏客户区计算，跟随任务栏移动/DPI/自动隐藏；挂载失败才回退到顶层覆盖窗口
- 停靠后是子窗口，拿不到 DWM 亚克力，所以定位后采样任务栏的实际像素颜色并画同色背景，视觉上等于没有背景（主窗口和悬浮窗仍然保留亚克力）
- Explorer 重启会让父任务栏和子窗口一起销毁，此时**不能对旧 WinUI Window 调 `Close()`**（会崩 coreclr），而是丢弃旧对象、重建窗口再挂载
- 卡片圆角 14、统计卡 12；首页 / 应用流量 / 设置是三个页面，用标题栏按钮和「←」返回切换，节点用两列卡片网格展示
- 图标使用 Bootstrap Icons（MIT）SVG，分类带独立颜色；实际流量用青色、计费流量用琥珀色，关键数字一眼可区分
- 网卡选择与统计信息已移入设置面板，首页底部只保留功能内容

## 项目结构

```
src/NetworkMonitor.Core/     采样、台账、统计、MonitoringService（无 UI 依赖）
  Proxy/                    Clash API 客户端、按进程/节点聚合、proxy_usage 台账
src/NetworkMonitor.WinUI/    WinUI 3 界面、托盘、悬浮窗、任务栏条
src/NetworkMonitor.Console/  命令行对账工具（与系统计数器比对用）
tools/make_icon.py     生成 Assets/networkmonitor.ico
docs/screenshot.png
```

## 构建与运行

```powershell
dotnet build NetworkMonitor.slnx -c Release -p:Platform=x64

.\src\NetworkMonitor.WinUI\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\NetworkMonitor.exe
```

`--minimized` 参数可只启动托盘（开机自启项用的就是这个）。

WinUI 非打包运行需要机器上装有 **Windows App Runtime 1.8**。要发给没装运行时的机器，用自包含发布：

```powershell
dotnet publish src/NetworkMonitor.WinUI -c Release -p:Platform=x64 -r win-x64 `
  -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -o dist
```

### 对账工具

```powershell
.\src\NetworkMonitor.Console\bin\Release\net10.0-windows\NetworkMonitor.Console.exe --seconds 15
```

会打印所有网卡、选中网卡、实时速率和台账汇总。开发时用下面的命令对比同一时间窗的增量，验证 P/Invoke 布局与统计口径：

```powershell
Get-NetAdapterStatistics | Select-Object Name, ReceivedBytes, SentBytes
```

## 版本、CI 与自动更新

当前版本 **1.1.0**，显示在「设置 → 版本」。

### CI

- `.github/workflows/build.yml`：push / PR 构建验证
- `.github/workflows/release.yml`：推送 `v*` tag（或手动触发）自动发布

发布流程会：

1. `dotnet publish` 自包含 `win-x64`（含 Windows App SDK，用户无需装运行时）
2. 用 Inno Setup 生成**免管理员**安装包 `NetworkMonitor-Setup-<version>.exe`（安装到 `%LOCALAPPDATA%\Programs\NetworkMonitor`）
3. 生成便携版 `NetworkMonitor-<version>-win-x64.zip`
4. 创建 GitHub Release，附上安装包和 zip，并自动生成 release notes

仓库地址已配置为 `https://github.com/TypeDreamMoon/NetworkMonitor`。CI 会把 `RepositoryUrl` 覆盖成当前仓库。发布命令：

```powershell
git tag v1.1.1
git push origin v1.1.1
```

### 自动更新

- 设置 → 版本 → 检查更新；「自动检查更新」默认开启
- 读取 GitHub 最新 release，只接受名字里带 `Setup` 的 `.exe`
- 下载到临时目录，如果 API 提供 `sha256` 会校验
- 静默运行安装包，安装完成后自动重启
- 选择「稍后」会记住跳过的版本，自动检查不再弹窗；手动检查仍会提示
- 本地构建如果还是 `OWNER` 占位，会显示「未配置更新仓库（RepositoryUrl）」
- 自动更新针对安装版；便携版会提示从 Releases 手动下载新的 zip

## 数据位置

`%APPDATA%\NetworkMonitor\`

- `networkmonitor.db` — 分钟级流量台账（自首次运行起长期保留，一年约 15 MB）
- `settings.json` — 界面与网卡选择
- `networkmonitor.log` — 异常日志

便携模式：exe 同目录放一个 `portable.txt`，或设置环境变量 `NETWORKMONITOR_HOME`。

## 已知限制 / 后续可做

- 任务栏条没有官方 API，默认是**停靠子窗口**（仅主任务栏、横向任务栏）；竖排/挂载失败时回退为顶层覆盖窗口
- 停靠模式没有亚克力（DWM 系统背景不作用于子窗口），背景用任务栏主题色近似
- 任务栏条不检测全屏应用（停靠模式下任务栏本身会随全屏隐藏；回退覆盖模式仍可能盖在全屏窗口上）
- Win11 默认把新托盘图标放进溢出区，需要用户在任务栏设置里固定
- 总计只覆盖 NetworkMonitor 运行期间。若要补系统级历史，可接 WinRT `NetworkUsage`（本机未验证）
- 按进程统计未实现：下一步用 ETW `Microsoft-Windows-Kernel-Network`（事件带 PID）
- 可加：流量包配额提醒、运营商口径（1000 进制）切换、月账单日设置

## License

暂未选择（建议 MIT）。第三方库许可见各自仓库。
