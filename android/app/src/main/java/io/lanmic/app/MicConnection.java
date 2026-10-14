package io.lanmic.app;

import java.io.Closeable;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;

/** Android-independent implementation of docs/PROTOCOL.md; also usable by integration tests. */
public final class MicConnection implements Closeable {
    public static final int FRAME_BYTES = 1920;
    public static final int TIMEOUT_MS = 5000;
    public enum Failure { BUSY, PROTOCOL }
    public static final class RejectedException extends IOException {
        public final Failure failure;
        public RejectedException(Failure failure, String message) { super(message); this.failure = failure; }
    }

    private final Object lock = new Object();
    private Socket socket;
    private volatile OutputStream output;
    private boolean closed;
    private final byte[] packet = new byte[FRAME_BYTES + 2];

    /** Single use. close() may be called from another thread during connect, handshake or writing. */
    public void connect(String host, int port, String deviceName) throws IOException {
        byte[] name = validDeviceName(deviceName).getBytes(StandardCharsets.UTF_8);
        Socket raw = new Socket();
        install(raw);
        boolean accepted = false;
        try {
            raw.setTcpNoDelay(true);
            raw.setKeepAlive(true);
            raw.setSendBufferSize(8192);
            raw.connect(new InetSocketAddress(host, port), TIMEOUT_MS);
            raw.setSoTimeout(TIMEOUT_MS);
            OutputStream out = raw.getOutputStream();
            byte[] magic = "LMIC0002".getBytes(StandardCharsets.US_ASCII);
            byte[] hello = new byte[magic.length + 2 + name.length];
            System.arraycopy(magic, 0, hello, 0, magic.length);
            hello[8] = (byte) (name.length >>> 8);
            hello[9] = (byte) name.length;
            System.arraycopy(name, 0, hello, 10, name.length);
            out.write(hello);
            out.flush();
            InputStream in = raw.getInputStream();
            int first = in.read(), second = in.read();
            if (first == -1 || second == -1) throw new EOFException("电脑在建立连接时关闭了连接");
            if (first == 'N' && second == 'O') throw new RejectedException(Failure.PROTOCOL, "电脑拒绝了协议或设备名称，请确认两端均为 LanMic v0.2 或更新版本");
            if (first == 'B' && second == 'Z') throw new RejectedException(Failure.BUSY, "电脑已连接另一台手机，请先停止另一台手机的传输");
            if (first != 'O' || second != 'K') throw new RejectedException(Failure.PROTOCOL, "电脑返回了不支持的协议，请确认两端均使用 LanMic v2 协议");
            synchronized (lock) {
                if (closed) throw new IOException("连接已停止");
                output = out;
            }
            accepted = true;
        } finally {
            if (!accepted) close();
        }
    }

    /** Send exactly 20 ms of little-endian mono PCM16. Call from one audio worker only. */
    public void sendPcmFrame(byte[] pcm) throws IOException {
        if (pcm == null || pcm.length != FRAME_BYTES) throw new IllegalArgumentException("PCM 帧必须是 1920 字节");
        OutputStream out = output;
        if (out == null) throw new IOException("电脑尚未接受连接");
        packet[0] = (byte) (FRAME_BYTES >>> 8);
        packet[1] = (byte) FRAME_BYTES;
        System.arraycopy(pcm, 0, packet, 2, FRAME_BYTES);
        out.write(packet);
    }

    @Override public void close() {
        Socket value;
        synchronized (lock) { closed = true; output = null; value = socket; socket = null; }
        if (value != null) try { value.close(); } catch (IOException ignored) { }
    }

    private void install(Socket value) throws IOException {
        synchronized (lock) {
            if (!closed) { socket = value; return; }
        }
        try { value.close(); } catch (IOException ignored) { }
        throw new IOException("连接已停止");
    }

    private static String validDeviceName(String name) {
        String value = name == null || name.trim().length() == 0 ? "Android" : name.trim();
        while (value.getBytes(StandardCharsets.UTF_8).length > 128)
            value = value.substring(0, value.offsetByCodePoints(value.length(), -1));
        return value;
    }

}
