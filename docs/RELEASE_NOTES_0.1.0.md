# LanMic 0.1.0 开发预览

安卓手机通过 Wi-Fi 把麦克风音频传到 Windows。独立实现，与 WO Mic 协议不兼容。

## 下载和使用

- **LanMic-Windows.zip**：解压后运行 LanMic.exe；Windows 10/11，.NET Framework 4.7.2+。
- **LanMic-android.apk**：传到 Android 8.0+ 手机安装，本地开发签名。
- **SHA256SUMS.txt**：安装包校验值。

两端同一局域网，电脑选择输出并开始监听，手机填写电脑 IP、端口和完整配对码后开始传输。

要供微信、游戏或会议软件当作麦克风使用，需另行安装 [VB-CABLE](https://vb-audio.com/Cable/)：LanMic 输出选 **CABLE Input**，目标应用麦克风选 **CABLE Output**。本版本不自带虚拟声卡驱动。

## 已实现

- 中文 Windows / Android 界面，48 kHz / PCM16 / 单声道音频。
- TLS 加密、证书指纹配对、错误认证限速与单手机连接。
- 手机麦克风前台服务、通知停止、静音和有限断线重连。
- 电脑输出设备选择、静音、音量、电平和有界播放缓冲。
- 固定工具链的一键源码构建、网络互通测试与打包脚本。

## 验证范围

21 项 C# TLS 集成断言、11 项生产 Java 客户端断言通过；跨语言确认 27 帧音频接收；Windows 实际音频输出的静音测试通过。APK 签名/对齐/入口校验通过。

**仍需安卓真机验证录音、锁屏、实际延迟及 VB-CABLE 到目标应用的完整链路。** 当前不包含 USB、蓝牙、iOS、自研虚拟声卡或自适应抖动缓冲。
