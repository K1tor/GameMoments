# 第三方组件与许可

GameMoments 本身以 MIT 许可发布（见 `LICENSE`）。发布包内附带以下第三方组件，它们各自保留原许可。

## FFmpeg 6.1.1（GPL v3）

- 文件：`ffmpeg/ffmpeg.exe`、`ffmpeg/ffprobe.exe`，以及 `ffmpeg/win32-x64.LICENSE`、`ffmpeg/win32-x64.README`
- 来源：FFmpeg 6.1.1 “essentials” Windows 构建（gyan.dev），通过 npmmirror 的 ffmpeg-static 镜像获取原始压缩包
- 构建信息：`ffmpeg version 6.1.1-essentials_build-www.gyan.dev`，配置含 `--enable-gpl --enable-libx264 --enable-nvenc --enable-amf --enable-mediafoundation --enable-libvpl`
- 许可：GNU General Public License v3（`ffmpeg/win32-x64.LICENSE` 为完整文本）
- 未对二进制做任何修改；仅作为独立子进程调用（未链接）
- 源码与构建脚本：<https://ffmpeg.org/download.html>、<https://www.gyan.dev/ffmpeg/builds/>
- 项目主页：<https://ffmpeg.org>

分发本软件时请保留上述许可文本与来源说明，并遵守 GPL 对二进制再分发的相应要求。

## NAudio 2.2.1（MIT）

- 用途：WASAPI loopback / 麦克风采集
- 许可：MIT License
- 项目主页：<https://github.com/naudio/NAudio>

## .NET 10 / WPF（MIT）

- 发布包为自包含（self-contained）Windows x64 构建，内含 .NET 运行时与 WPF 程序集
- 许可：MIT License
- 项目主页：<https://github.com/dotnet/runtime>、<https://github.com/dotnet/wpf>

## 其他

图标为代码绘制的占位图形，未使用第三方美术资源。界面字体使用系统自带的“Microsoft YaHei UI”。
