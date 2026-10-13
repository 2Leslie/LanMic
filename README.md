# LanMic

把安卓手机当作 Windows 电脑的 Wi-Fi 麦克风。以 WO Mic 的使用场景为参考，独立实现手机采集和电脑接收，不兼容 WO Mic 协议。

[下载 Windows 程序和 Android APK](https://github.com/2Leslie/LanMic/releases) · [源码仓库](https://github.com/2Leslie/LanMic)

第一版面向 **Android 8.0+ / Windows 10、11**：原生安卓 App、原生 Windows 窗口、局域网 TLS 加密传输、48 kHz / 16-bit / 单声道、静音、电平、输出设备选择。无需登录或云服务。手机与电脑需处于可互相访问的局域网。

> **作为其他软件的麦克风使用，需要通用虚拟声卡。** 本项目当前没有自研虚拟麦克风驱动；仅把音频播放到扬声器，不等于在 Windows 创建了麦克风。

## 第一次使用

1. 打开 `dist/LanMic.exe`。Windows 需安装 .NET Framework 4.7.2 或更新版本（Windows 10/11 通常已包含）。
2. 将 `dist/LanMic-android.apk` 传到安卓手机并安装。APK 是本地开发签名，未上架应用商店。
3. 电脑与手机连接同一路由器。电脑可以插网线；不要求两端都使用无线连接。手机不要用移动数据或隔离访客 Wi-Fi。
4. 在电脑选择输出设备并点击开始监听。把电脑显示的 **局域网 IPv4 地址、端口、完整配对码** 填到手机 App。不要选 VPN/代理虚拟网卡地址或 `169.254.*` 地址。默认端口 `18765`。
5. 手机点击开始，授予麦克风权限。Android 13+ 也建议允许通知，以便从通知栏停止。连接后，对手机讲话，观察电脑接收帧数与电平。
6. 结束时，手机点停止或通知栏的停止；电脑点停止监听。手机静音时仍保持连接并发送静音帧。

每次重新开始电脑监听，配对码会变化，需要更新手机输入。配对码中的连字符和空格可以省略。它同时包含证书指纹和连接验证码；请完整填写。

## 接入微信、游戏、会议软件

建议首版使用 [VB-Audio 官方 VB-CABLE](https://vb-audio.com/Cable/)。它是独立的第三方虚拟音频驱动，请从官网查看安装说明与许可；本项目不打包或自动安装它。

1. 安装 VB-CABLE，按安装程序要求重启电脑。
2. 在 **LanMic 电脑端的输出设备** 中选择 **CABLE Input**。
3. 在 **微信、OBS、游戏或会议软件的麦克风/输入设备** 中选择 **CABLE Output**。

流向为：`手机麦克风 → LanMic → CABLE Input（播放端）→ CABLE Output（录音端）→ 目标软件`。

名称中的 Input / Output 是相对于虚拟线缆，不是相对于 LanMic。原有 **WO Mic Device** 驱动不能直接作为 LanMic 的音频输入接口。

只想先确认传输是否有声音，可选择耳机输出进行试听。建议戴耳机，以免电脑扬声器的声音被手机再次采集形成回声。选择“仅电平”只验证收到音频，不向其他软件提供麦克风。

## 连不上或没有声音

| 现象 | 检查 |
| --- | --- |
| 连接超时 | 先启动电脑监听；确认电脑 IPv4；手机和电脑同网段可互访；检查访客 Wi-Fi/AP 隔离或代理虚拟网卡。 |
| Windows 防火墙提示 | 在信任的家庭/工作局域网允许 LanMic 的专用网络入站连接。无需开放公网端口，不需要路由器端口转发。 |
| 配对码或证书不匹配 | 从电脑重新填写完整配对码；重启监听会换码。连续输错 5 次会触发 30 秒限速，请稍后再试。 |
| 提示已被占用 | 停止已连接的另一台手机，再连接。 |
| 电脑有电平，目标软件无声 | LanMic 输出选 CABLE Input，目标软件输入选 CABLE Output；检查双方静音/音量以及 Windows 麦克风访问权限。 |
| 锁屏后中断 | 查看前台服务通知是否仍存在；在手机系统中允许 LanMic 后台运行，关闭该 App 的电池优化（各品牌选项不同）。 |
| 输出设备错误 | 停止监听，重新选择可用输出；不会自动切换到扬声器。 |
| 无声卡 | 可以在仅电平模式验证网络；需安装可用输出设备/虚拟声卡才能传给其他软件。 |

## 从源码构建

在项目根目录的 PowerShell 中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-tools.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

工具链仅下载和解压到本项目的 `.tools/`，不要求安装 Android Studio、Gradle 或全局 Java。Windows 使用系统附带的 C# 编译器；Android 使用固定版本的 JDK、Android API 35 和 Build Tools。下载地址、版本及校验值见 `scripts/toolchain.lock.json`。构建产物在 `dist/`，中间文件在 `build/`，本地 APK 签名密钥在 `.local/`。

保留 `.local/` 的签名密钥才能用后续开发版 APK 覆盖安装；删除后重新生成的密钥会导致安卓要求先卸载旧版。密钥、下载工具和编译产物均已加入 `.gitignore`。

验证与打包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

测试会使用本机回环网络验证真实 TLS 和 Java/C# 音频协议互通，并向一个可用播放设备发送短暂的**静音**以验证输出与资源释放。可用 `-SkipAudioOutput` 跳过设备测试；测试不打开任何麦克风。手机实录仍需真机验证。

目录：

```text
android/app/src/main/   安卓界面、麦克风前台服务、TLS 客户端
windows/               Windows 界面、TLS 服务端、waveOut 音频输出
scripts/               工具链下载与构建
tests/                 网络协议和跨语言集成检查
docs/PROTOCOL.md       协议与音频格式
docs/VALIDATION.md     本机验证结果与未验证范围
```

## 当前范围

- 首版支持安卓到 Windows 的单路 Wi-Fi 音频，不包含 USB、蓝牙、iOS、Opus、自动发现或自研虚拟声卡。
- 原始 PCM 带宽约 768 kbit/s，另有 TLS 和网络开销。每个音频帧 20 ms，这**不是**端到端延迟承诺；实际延迟还包括采集、网络、播放缓冲和目标软件。
- 接收端有界缓冲避免无限堆积；TCP 在丢包时可能等待重传，网络拥挤时会出现卡顿。
- 手机厂商后台限制、电话打断、硬件采样和实际音质需通过真机测试确认。
- 无自动开机启动、录音文件或遥测；不修改系统默认音频设备。启用监听才接受连接，停止后关闭连接。

参考：[WO Mic 官方架构](https://wolicheng.com/womic/)、[Android 麦克风前台服务](https://developer.android.com/develop/background-work/services/fgs/service-types#microphone)、[VB-CABLE 官方说明](https://vb-audio.com/Cable/)。
