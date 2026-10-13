# LanMic v1 wire protocol

Independent implementation; not compatible with WO Mic's proprietary protocol.

## Transport and pairing

- Windows listens on TCP port **18765** (IPv4); Android initiates the connection on the same LAN.
- TLS 1.2 using a self-signed RSA certificate generated afresh by Windows for each listener session. No certificate-store installation. On .NET Framework, export and re-import the certificate with `UserKeySet` (without `PersistKeySet`) so Windows SChannel can acquire the private key; dispose it after the last session socket closes.
- Windows displays a pairing token consisting of the first **20 hexadecimal characters of SHA-256(DER certificate)** plus a random **6 decimal digit** authentication code. Example format: `ABCD-EF01-2345-6789-ABCD-123456`. Normalize by removing spaces and hyphens and uppercase; exactly 26 characters. The first 20 are the certificate pin; the last 6 are the authentication code. New token on each new listener session. Do not log the token.
- Android MUST verify the pinned certificate digest during TLS handshake; no permissive trust manager. Reject wrong pin before sending the authentication code or opening AudioRecord. Both endpoints require TLS 1.2. No cloud service.
- The Windows listener starts ONLY on an explicit Start button. Android records ONLY after microphone permission and explicit Start in a visible Activity, using a microphone foreground service.

## Handshake (inside TLS)

Client sends, in order:

1. 8 ASCII bytes `LMIC0001`.
2. 6 ASCII decimal bytes authentication code.
3. Unsigned 16-bit big-endian UTF-8 device-name length, 1..128.
4. Device-name bytes.

Server responds with exactly two ASCII bytes: `OK` (accepted), `NO` (invalid handshake/auth), or `BZ` (another authenticated source connected).

Accept only one authenticated sender. Bound pending handshakes and set 5-second handshake/read timeouts. Connection rejection must not interrupt the active source. An idle unauthenticated socket must not prevent a valid sender connecting. Android shows distinct pin/auth/busy/connection errors. Clean Stop cancels pending sockets and capture. Auto reconnect uses bounded retries and stops on pin/auth/busy errors.

## Audio frames

After `OK`, fixed PCM **48000 Hz, signed 16-bit little-endian, mono**, **960 samples / 1920 bytes / 20 ms** per frame. Each frame: uint16 BE byte length (must be 1920) then 1920 PCM bytes. TCP preserves order; read fully across partial reads. Reject other lengths before allocating. No audio is written to disk by default. Muting transmits zeros (connection stays alive).

Windows queues a bounded number of frames (maximum 120 ms) for waveOut playback, drops stale queued audio when overloaded, computes actual RMS/peak from PCM, and clears buffers on disconnect. There is no adaptive jitter buffer or target prefill in v1; irregular arrivals can underflow the audio device. UI must distinguish listening, authenticated connection, received frames, and completed output buffers. Explicit output selection; prefer a detected CABLE Input / virtual playback endpoint. A normal speaker/headphone output is a monitor, not a system microphone. No silent output-device fallback after failure.

## Build constraints

- Windows: C# 5 / .NET Framework 4.7.2+ (built-in v4 csc on this machine), WinForms, no NuGet dependency. CertificateRequest + RSACng is available and verified here. Native winmm waveOut for output; carefully retain WAVEHDR/data memory until returned.
- Android: plain Java (source/target 8), minimum API 26, target/compile API 35, platform APIs only. Manual reproducible build via javac, aapt2, d8, zipalign, apksigner; Gradle/Android Studio optional, not required. Package `io.lanmic.app`; name `LanMic`.
- Workspace-local build tools under `.tools/`, intermediates under `build/`, deliverables under `dist/`.

## Safety and scope

No automatic driver installation, firewall changes, default-audio-device changes, boot startup or remote telemetry. Offer an official VB-CABLE link and clear setup steps. First release is an Android/Windows Wi-Fi MVP, not complete WO Mic feature parity. Actual handset capture, OEM background behavior and end-to-end microphone latency require a real device test.
