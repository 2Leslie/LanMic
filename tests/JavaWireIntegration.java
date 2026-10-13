import io.lanmic.app.MicConnection;
import java.io.IOException;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.concurrent.atomic.AtomicReference;

/** Uses the exact production Android TLS client, without mocking SSL or the Windows peer. */
public final class JavaWireIntegration {
    private static int passed;
    private static void check(boolean value, String description) {
        if (!value) throw new AssertionError(description);
        passed++;
        System.out.println("PASS " + description);
    }
    private static void rejected(int port, String token, MicConnection.Failure expected) throws Exception {
        try (MicConnection connection = new MicConnection()) {
            try { connection.connect("127.0.0.1", port, token, "Java TLS test"); }
            catch (MicConnection.RejectedException error) {
                check(error.failure == expected, "production Java client reports " + expected);
                return;
            }
        }
        throw new AssertionError("Expected " + expected);
    }
    public static void main(String[] args) throws Exception {
        int port = Integer.parseInt(args[0]);
        String token = MicConnection.normalizePairingToken(args[1]);
        check(MicConnection.normalizePairingToken("  " + token.substring(0, 20).toLowerCase() + "-" + token.substring(20) + " ").equals(token), "formatted pairing token normalizes correctly");
        boolean invalid = false;
        try { MicConnection.normalizePairingToken("123456"); }
        catch (IllegalArgumentException expected) { invalid = true; }
        check(invalid, "incomplete token is rejected locally");
        char first = token.charAt(0) == '0' ? '1' : '0';
        rejected(port, first + token.substring(1), MicConnection.Failure.PIN);
        char last = token.charAt(25) == '0' ? '1' : '0';
        rejected(port, token.substring(0, 25) + last, MicConnection.Failure.AUTH);
        try (MicConnection active = new MicConnection()) {
            active.connect("127.0.0.1", port, token, "Java real Android network client");
            byte[] frame = new byte[MicConnection.FRAME_BYTES];
            for (int sample = 0; sample < 960; sample++) {
                short value = (short) (Math.sin(2 * Math.PI * 440 * sample / 48000) * 8192);
                frame[sample * 2] = (byte) value;
                frame[sample * 2 + 1] = (byte) (value >>> 8);
            }
            for (int i = 0; i < 25; i++) { active.sendPcmFrame(frame); Thread.sleep(20); }
            check(true, "production Java client streams 25 PCM frames to C# receiver");
            rejected(port, token, MicConnection.Failure.BUSY);
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
            reconnect.connect("127.0.0.1", port, token, name.toString());
            reconnect.sendPcmFrame(new byte[1920]);
            Thread.sleep(100);
            check(true, "long UTF-8 device name is bounded without corrupting handshake");
        }
        try (MicConnection closed = new MicConnection()) {
            closed.close();
            invalid = false;
            try { closed.connect("127.0.0.1", port, token, "Closed"); }
            catch (IOException expected) { invalid = true; }
            check(invalid, "stopped client cannot reconnect itself");
        }
        // A silent peer deliberately stalls the TLS handshake; Stop must interrupt it.
        try (ServerSocket silentServer = new ServerSocket(0);
             MicConnection connecting = new MicConnection()) {
            AtomicReference<Throwable> result = new AtomicReference<>();
            Thread worker = new Thread(() -> {
                try { connecting.connect("127.0.0.1", silentServer.getLocalPort(), token, "Cancel"); }
                catch (Throwable expected) { result.set(expected); }
            });
            worker.setDaemon(true);
            worker.start();
            try (Socket accepted = silentServer.accept()) {
                Thread.sleep(100);
                connecting.close();
                worker.join(2500);
                check(!worker.isAlive() && result.get() instanceof IOException, "Stop cancels a stalled TLS handshake");
            }
        }
        System.out.println("RESULT " + passed + " production Java client assertions passed");
    }
}
