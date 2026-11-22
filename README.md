# B站视频下载器

基于 Windows Forms 的便携下载器。`MainWindow.cs` 管理界面和下载状态，`DownloadEngine.cs` 调用 yt-dlp 和 FFmpeg。若 yt-dlp 的网页入口返回 HTTP 412，`BilibiliPlayer.cs` 会尝试从 B 站的公开嵌入播放器读取当前分 P 的画面和音频。

## 重新打包

在 Windows x64 上，用系统自带的 .NET Framework C# 编译器运行 `Build.ps1`。将 yt-dlp、FFmpeg 和 FFprobe 的 Windows x64 可执行文件放在同一目录，文件名分别为 `yt-dlp.exe`、`ffmpeg.exe`、`ffprobe.exe`。同目录需包含 `yt-dlp-LICENSE.txt`、`FFmpeg-LICENSE.txt` 和记录组件版本与来源的 `versions.txt`。现有便携包的 `tools` 目录可直接用来重新打包。

```powershell
.\Build.ps1 -ToolsDirectory 'C:\path\to\tools'
```

`Build.ps1` 默认在源码目录生成 `B站视频下载器-Windows-x64-v1.0.1.zip`。如需自定义位置，可使用 `-OutputPath`。
