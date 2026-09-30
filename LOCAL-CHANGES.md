# 本地改动说明（AxolotlXD666 的 fork）

这份 fork 相对上游 The-MuffinDev/LyricsOnTheGo 改了 5 个文件，全部是为了在本机配合
"从本地 Spotify 客户端取歌词"的悬浮窗使用。上游 MIT 许可，改动同样以 MIT 发布。

## 改了什么

### 1. 去掉毛玻璃，背景不透明度=0 就只剩歌词（Glass/GlassHost.cs）
- 删掉 `CreateHostBackdropBrush()` 那一段。原来那个窗口是"桌面背景被系统模糊 + 一层颜色"，
  而设置里的"背景不透明度"只控制颜色层，所以调到 0 看到的仍然是满屏毛玻璃。
- 现在这个窗口只画颜色层：`背景不透明度=0` = 完全透明；40~60 = 纯色半透明，没有任何模糊。

### 2. 查不到歌词时持续重试（Services/LyricsController.cs）
- 原来每首歌只查一次（2 次尝试、间隔 2.5 秒），查不到就一直显示"歌词未找到"。
- 现在空着的时候每 400 毫秒重试一次，最多 60 秒，或直到切歌。歌词晚到也会自动显示。

### 3. 置顶：不被任务栏或别的应用压住（Interop/Native.cs + MainWindow.xaml.cs）
- 任务栏本身也是置顶窗口，点它会被提到置顶带最上面；点别的应用同样会把那个应用提上去。
- 新增 `Native.BringToTop()` / `Native.IsCovered()`：沿层叠顺序往上找**别的进程的可见窗口**，
  发现被压就 `SetWindowPos(HWND_TOPMOST)` 顶回来。
- 33 毫秒的定时器 + 窗口消息（`WM_WINDOWPOSCHANGED` 无 `SWP_NOZORDER`、`WM_ACTIVATEAPP`、
  `TaskbarCreated`）双路触发，实测恢复时间 1~45 毫秒。
- 排除 IME/缩略图等辅助窗口（否则会挡住中文输入法候选框），空闲 CPU 约 0.3%。

### 4. 启动即应用背景色（MainWindow.xaml.cs）
- `GlassHost` 原来把 tint 写死 0.35，保存的 0 要等有人推一次设置才生效，
  于是每次启动都先显示一层 35% 的底。现在玻璃窗口起来后立刻推一次设置，之后定时器再推。

### 5. 尺寸与淡出微调（MainWindow.xaml + MainWindow.xaml.cs）
- `MinHeight` 120 → 60 DIP；`MaxHeight` 133 DIP（本机 150% 缩放 = 400 物理像素）。
- 上下边缘淡出 `EdgeFadePx` 30 → 15。
- 媒体会话轮询 1 秒 → 200 毫秒（原来切歌后"Searching for lyrics"最少要挂 1 秒）。
- 恢复窗口位置时夹在工作区内。

### 6. global.json
- `rollForward` 从 `latestFeature` 改成 `latestMajor`，这样只有 .NET 10 SDK 的机器也能直接编译。

## 怎么用

1. `dotnet build src/LyricsOnTheGo/LyricsOnTheGo.csproj -c Release`
2. 把生成的 `LyricsOnTheGo.dll` 覆盖到安装目录（自包含安装只需要换这一个文件）
3. 重启程序

配套的另一半（把歌词喂进来的那套：注入 Spotify 客户端 → 本机桥接 → 写库/写缓存）
不在这个仓库里，在本机的另一个工作区。
