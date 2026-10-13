package io.lanmic.app;

import java.io.Closeable;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.Arrays;
import java.util.Locale;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLHandshakeException;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/** Android-independent implementation of docs/PROTOCOL.md; also usable by integration tests. */
public final class MicConnection implements Closeable {
    public static final int FRAME_BYTES = 1920;
    public static final int TIMEOUT_MS = 5000;
    public enum Failure { PIN, AUTH, BUSY, PROTOCOL }
    public static final class RejectedException extends IOException {
        public final Failure failure;
        public RejectedException(Failure failure, String message) { super(message); this.failure = failure; }
    }

    private final Object lock = new Object();
    private Socket socket;
    private volatile OutputStream output;
    private boolean closed;
    private final byte[] packet = new byte[FRAME_BYTES + 2];

    public static String normalizePairingToken(String token) {
        if (token == null) throw new IllegalArgumentException("请输入电脑上显示的配对码");
        String value = token.replaceAll("[\\s-]", "").toUpperCase(Locale.ROOT);
        if (!value.matches("[0-9A-F]{20}[0-9]{6}"))
            throw new IllegalArgumentException("配对码应包含 20 位十六进制指纹和 6 位数字；请完整输入电脑显示的配对码");
        return value;
    }

    /** Single use. close() may be called from another thread during connect, handshake or writing. */
    public void connect(String host, int port, String pairingToken, String deviceName) throws IOException {
        String token = normalizePairingToken(pairingToken);
        byte[] pin = new byte[10];
        for (int i = 0; i < pin.length; i++) pin[i] = (byte) Integer.parseInt(token.substring(i * 2, i * 2 + 2), 16);
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
            SSLContext context = SSLContext.getInstance("TLSv1.2");
            context.init(null, new TrustManager[] { new PinTrustManager(pin) }, new SecureRandom());
            SSLSocket tls = (SSLSocket) context.getSocketFactory().createSocket(raw, host, port, true);
            install(tls);
            tls.setEnabledProtocols(new String[] { "TLSv1.2" });
            tls.setSoTimeout(TIMEOUT_MS);
            tls.startHandshake(); // The pin is checked here, before the authentication code is sent.
            OutputStream out = tls.getOutputStream();
            byte[] magic = "LMIC0001".getBytes(StandardCharsets.US_ASCII);
            byte[] auth = token.substring(20).getBytes(StandardCharsets.US_ASCII);
            byte[] hello = new byte[magic.length + auth.length + 2 + name.length];
            System.arraycopy(magic, 0, hello, 0, magic.length);
            System.arraycopy(auth, 0, hello, magic.length, auth.length);
            hello[14] = (byte) (name.length >>> 8);
            hello[15] = (byte) name.length;
            System.arraycopy(name, 0, hello, 16, name.length);
            out.write(hello);
            out.flush();
            InputStream in = tls.getInputStream();
            int first = in.read(), second = in.read();
            if (first == -1 || second == -1) throw new EOFException("电脑在配对期间关闭了连接");
            if (first == 'N' && second == 'O') throw new RejectedException(Failure.AUTH, "配对码认证失败，请重新输入电脑当前显示的配对码");
            if (first == 'B' && second == 'Z') throw new RejectedException(Failure.BUSY, "电脑已连接另一台手机，请先停止另一台手机的传输");
            if (first != 'O' || second != 'K') throw new RejectedException(Failure.PROTOCOL, "电脑返回了不支持的协议，请确认两端均为 LanMic v1");
            synchronized (lock) {
                if (closed) throw new IOException("连接已停止");
                output = out;
            }
            accepted = true;
        } catch (GeneralSecurityException e) {
            throw new IOException("无法初始化安全连接", e);
        } catch (SSLHandshakeException e) {
            if (hasPinFailure(e)) throw new RejectedException(Failure.PIN, "电脑证书指纹不匹配；请核对电脑地址并重新输入当前配对码");
            throw e;
        } finally {
            if (!accepted) close();
        }
    }

    /** Send exactly 20 ms of little-endian mono PCM16. Call from one audio worker only. */
    public void sendPcmFrame(byte[] pcm) throws IOException {
        if (pcm == null || pcm.length != FRAME_BYTES) throw new IllegalArgumentException("PCM 帧必须是 1920 字节");
        OutputStream out = output;
        if (out == null) throw new IOException("连接尚未通过认证");
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

    private static boolean hasPinFailure(Throwable error) {
        for (Throwable cause = error; cause != null; cause = cause.getCause())
            if (cause instanceof PinCertificateException) return true;
        return false;
    }

    private static final class PinCertificateException extends CertificateException {
        PinCertificateException(String message) { super(message); }
    }

    private static final class PinTrustManager implements X509TrustManager {
        private final byte[] expected;
        PinTrustManager(byte[] expected) { this.expected = expected.clone(); }
        @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
        @Override public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            throw new CertificateException("不支持客户端证书");
        }
        @Override public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            if (chain == null || chain.length == 0) throw new PinCertificateException("Missing server certificate");
            try {
                byte[] digest = MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded());
                if (!MessageDigest.isEqual(expected, Arrays.copyOf(digest, expected.length)))
                    throw new PinCertificateException("Server certificate pin mismatch");
            } catch (GeneralSecurityException e) {
                if (e instanceof PinCertificateException) throw (PinCertificateException) e;
                throw new CertificateException("无法验证电脑证书", e);
            }
        }
    }
}
