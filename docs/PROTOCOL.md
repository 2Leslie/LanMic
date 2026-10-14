# LanMic v2 wire protocol (app 0.2.0)

Independent implementation; not compatible with WO Mic's proprietary protocol.

## Transport

- Windows listens on TCP port **18765** (IPv4); Android initiates the connection on the same LAN.
- Plain TCP, with no encryption, certificate, password or pairing token. The user enters only the computer IP and port. No cloud service.
- Protocol v2 is deliberately incompatible with app 0.1.0's v1 transport. Both apps must be updated together. There is no automatic fallback to another transport.
- The Windows listener starts ONLY on an explicit Start button. Android records ONLY after microphone permission and explicit Start in a visible Activity, using a microphone foreground service.

## Handshake (directly on the TCP stream)

Client sends, in order:

1. 8 ASCII bytes `LMIC0002`.
2. Unsigned 16-bit big-endian UTF-8 device-name length, 1..128.
3. Device-name bytes. The name is only a display label, not a credential.

Server responds with exactly two ASCII bytes: `OK` (accepted), `NO` (invalid protocol or device name), or `BZ` (another source connected). Android opens AudioRecord only after receiving `OK`.

Accept only one sender. Bound pending handshakes to eight and set 5-second handshake/read timeouts. Connection rejection must not interrupt the active source. An idle socket must not prevent a valid sender connecting. Android shows distinct protocol/busy/connection errors. Clean Stop cancels pending sockets and capture. Auto reconnect uses bounded retries and stops on protocol/busy errors.

## Audio frames

After `OK`, fixed PCM **48000 Hz, signed 16-bit little-endian, mono**, **960 samples / 1920 bytes / 20 ms** per frame. Each frame: uint16 BE byte length (must be 1920) then 1920 PCM bytes. TCP preserves order; read fully across partial reads. Reject other lengths before allocating. No audio is written to disk by default. Muting transmits zeros (connection stays alive).

Windows queues a bounded number of frames (maximum 120 ms) for waveOut playback, drops stale queued audio when overloaded, computes actual RMS/peak from PCM, and clears buffers on disconnect. There is no adaptive jitter buffer or target prefill; irregular arrivals can underflow the audio device. UI must distinguish listening, connected sender, received frames, and completed output buffers. Explicit output selection; prefer a detected CABLE Input / virtual playback endpoint. A normal speaker/headphone output is a monitor, not a system microphone. No silent output-device fallback after failure.

## Build constraints

- Windows: C# 5 / .NET Framework 4.7.2+ (built-in v4 csc on this machine), WinForms, no NuGet dependency. Native winmm waveOut for output; carefully retain WAVEHDR/data memory until returned.
- Android: plain Java (source/target 8), minimum API 26, target/compile API 35, platform APIs only. Manual reproducible build via javac, aapt2, d8, zipalign, apksigner; Gradle/Android Studio optional, not required. Package `io.lanmic.app`; name `LanMic`.
- Workspace-local build tools under `.tools/`, intermediates under `build/`, deliverables under `dist/`.

## Safety and scope

No automatic driver installation, firewall changes, default-audio-device changes, boot startup or remote telemetry. Offer an official VB-CABLE link and clear setup steps. First release is an Android/Windows Wi-Fi MVP, not complete WO Mic feature parity. Actual handset capture, OEM background behavior and end-to-end microphone latency require a real device test.
