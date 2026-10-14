import io.lanmic.app.MicConnection;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicReference;

/** Uses the exact production Android TCP client and the real Windows receiver. */
public final class JavaWireIntegration {
    private static int passed;
    private static void check(boolean value, String description) {
        if (!value) throw new AssertionError(description);
        passed++;
        System.out.println("PASS " + description);
    }
    private static void rejected(int port, MicConnection.Failure expected) throws Exception {
        try (MicConnection connection = new MicConnection()) {
            try { connection.connect("127.0.0.1", port, "Java TCP test"); }
            catch (MicConnection.RejectedException error) {
                check(error.failure == expected, "production Java client reports " + expected);
                return;
            }
        }
        throw new AssertionError("Expected " + expected);
    }
    private static byte[] readFully(InputStream stream, int length) throws IOException {
        byte[] bytes = new byte[length];
        int offset = 0;
        while (offset < bytes.length) {
            int count = stream.read(bytes, offset, bytes.length - offset);
            if (count < 0) throw new IOException("Unexpected EOF in client handshake");
            offset += count;
        }
        return bytes;
    }
    private static void verifyPlainHandshakeAndRejection() throws Exception {
        // The real client's first ten bytes are magic and name length. The peer
        // immediately replies after the name without asking for credentials.
        try (ServerSocket peer = new ServerSocket(0)) {
            peer.setSoTimeout(3000);
            AtomicReference<Throwable> result = new AtomicReference<>();
            Thread server = new Thread(() -> {
                try (Socket accepted = peer.accept()) {
                    accepted.setSoTimeout(3000);
                    InputStream input = accepted.getInputStream();
                    byte[] header = readFully(input, 10);
                    if (!new String(header, 0, 8, StandardCharsets.US_ASCII).equals("LMIC0002"))
                        throw new AssertionError("Client did not send plaintext TCP v2 magic");
                    int length = ((header[8] & 255) << 8) | (header[9] & 255);
                    if (length != 4 || !new String(readFully(input, length), StandardCharsets.UTF_8).equals("Wire"))
                        throw new AssertionError("Client sent unexpected fields between magic and device name");
                    OutputStream output = accepted.getOutputStream();
                    output.write('N');
                    output.flush();
                    Thread.sleep(30);
                    output.write('O');
                    output.flush();
                } catch (Throwable error) { result.set(error); }
            });
            server.setDaemon(true);
            server.start();
            try (MicConnection connection = new MicConnection()) {
                boolean denied = false;
                try { connection.connect("127.0.0.1", peer.getLocalPort(), "Wire"); }
                catch (MicConnection.RejectedException error) { denied = error.failure == MicConnection.Failure.PROTOCOL; }
                check(denied, "production Java client reports fragmented NO response as PROTOCOL");
            }
            server.join(3500);
            if (result.get() != null) throw new AssertionError("Raw TCP handshake inspection failed", result.get());
            check(!server.isAlive(), "production Java client sends plaintext magic and name without any password");
        }
    }
    public static void main(String[] args) throws Exception {
        int port = Integer.parseInt(args[0]);
        verifyPlainHandshakeAndRejection();
        boolean invalid;
        try (MicConnection active = new MicConnection()) {
            active.connect("127.0.0.1", port, "Java real Android network client");
            byte[] frame = new byte[MicConnection.FRAME_BYTES];
            for (int sample = 0; sample < 960; sample++) {
                short value = (short) (Math.sin(2 * Math.PI * 440 * sample / 48000) * 8192);
                frame[sample * 2] = (byte) value;
                frame[sample * 2 + 1] = (byte) (value >>> 8);
            }
            for (int i = 0; i < 25; i++) { active.sendPcmFrame(frame); Thread.sleep(20); }
            check(true, "production Java client streams 25 PCM frames to C# receiver");
            rejected(port, MicConnection.Failure.BUSY);
            active.sendPcmFrame(new byte[1920]);
            check(true, "existing Java connection survives competing sender rejection");
            invalid = false;
            try { active.sendPcmFrame(new byte[2]); }
            catch (IllegalArgumentException expected) { invalid = true; }
            check(invalid, "production sender rejects malformed PCM size");
            Thread.sleep(100);
        }
        Thread.sleep(150);
        try (MicConnection reconnect = new MicConnection()) {
            StringBuilder name = new StringBuilder();
            for (int i = 0; i < 100; i++) name.append("\uD83D\uDCF1");
            reconnect.connect("127.0.0.1", port, name.toString());
            reconnect.sendPcmFrame(new byte[1920]);
            Thread.sleep(100);
            check(true, "long UTF-8 device name is bounded without corrupting handshake");
        }
        try (MicConnection closed = new MicConnection()) {
            closed.close();
            invalid = false;
            try { closed.connect("127.0.0.1", port, "Closed"); }
            catch (IOException expected) { invalid = true; }
            check(invalid, "stopped client cannot reconnect itself");
        }
        // A silent peer accepts TCP but never sends OK. Stop must unblock read.
        try (ServerSocket silentServer = new ServerSocket(0);
             MicConnection connecting = new MicConnection()) {
            silentServer.setSoTimeout(3000);
            AtomicReference<Throwable> result = new AtomicReference<>();
            Thread worker = new Thread(() -> {
                try { connecting.connect("127.0.0.1", silentServer.getLocalPort(), "Cancel"); }
                catch (Throwable expected) { result.set(expected); }
            });
            worker.setDaemon(true);
            worker.start();
            try (Socket accepted = silentServer.accept()) {
                Thread.sleep(100);
                connecting.close();
                worker.join(2500);
                check(!worker.isAlive() && result.get() instanceof IOException, "Stop cancels a stalled TCP protocol handshake");
            }
        }
        System.out.println("RESULT " + passed + " production Java client assertions passed");
    }
}
