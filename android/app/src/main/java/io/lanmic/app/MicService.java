package io.lanmic.app;

import android.Manifest;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.os.SystemClock;
import java.io.IOException;
import java.net.ConnectException;
import java.net.SocketTimeoutException;
import java.net.UnknownHostException;
import java.util.ArrayList;

/** The foreground service owns the microphone and all network resources. */
public final class MicService extends Service {
    public static final String START = "io.lanmic.app.START";
    public static final String STOP = "io.lanmic.app.STOP";
    public static final String MUTE = "io.lanmic.app.MUTE";
    private static final String CHANNEL = "lanmic_microphone";
    private static final int NOTIFICATION_ID = 1;
    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static final ArrayList<Observer> OBSERVERS = new ArrayList<Observer>();
    private static volatile State latest = new State(false, false, false, "准备连接", "先在电脑上启动 LanMic 接收端", 0, 0);

    public interface Observer { void onMicState(State state); }
    public static final class State {
        public final boolean running, connected, muted;
        public final String title, detail;
        public final int level;
        public final long frames;
        State(boolean running, boolean connected, boolean muted, String title, String detail, int level, long frames) {
            this.running = running; this.connected = connected; this.muted = muted;
            this.title = title; this.detail = detail; this.level = level; this.frames = frames;
        }
    }
    public static State state() { return latest; }
    // Activity subscription and state publication always happen on the main thread.
    public static void observe(Observer observer) { if (!OBSERVERS.contains(observer)) OBSERVERS.add(observer); observer.onMicState(latest); }
    public static void unobserve(Observer observer) { OBSERVERS.remove(observer); }
    private static void publish(State state) {
        latest = state;
        for (Observer observer : new ArrayList<Observer>(OBSERVERS)) observer.onMicState(state);
    }

    private volatile Session current;
    private NotificationManager notifications;
    private final Runnable watchdog = new Runnable() {
        @Override public void run() {
            Session session = current;
            if (session != null && session.watchdogArmed && !session.cancelled
                    && SystemClock.elapsedRealtime() - session.lastNetworkProgress > 7000) session.abortStalledNetwork();
            MAIN.postDelayed(this, 1000);
        }
    };

    @Override public void onCreate() {
        super.onCreate();
        notifications = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        NotificationChannel channel = new NotificationChannel(CHANNEL, "麦克风传输", NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("显示麦克风传输状态，随时停止录音");
        channel.setSound(null, null);
        notifications.createNotificationChannel(channel);
        MAIN.post(watchdog);
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) { stopSelf(); return START_NOT_STICKY; }
        String action = intent.getAction();
        if (STOP.equals(action)) { stopCapture(); return START_NOT_STICKY; }
        if (MUTE.equals(action)) {
            Session session = current;
            if (session != null) {
                session.muted = intent.getBooleanExtra("muted", false);
                State previous = latest;
                publish(new State(true, previous.connected, session.muted, previous.title, previous.detail, 0, previous.frames));
                notifications.notify(NOTIFICATION_ID, notification(session.muted ? "麦克风已静音" : previous.title));
            } else stopSelf();
            return START_NOT_STICKY;
        }
        if (!START.equals(action)) { if (current == null) stopSelf(); return START_NOT_STICKY; }
        if (current != null) return START_NOT_STICKY;
        String host = intent.getStringExtra("host");
        int port = intent.getIntExtra("port", 18765);
        try {
            if (host == null || host.length() == 0 || port < 1 || port > 65535) throw new IllegalArgumentException("电脑地址或端口不正确");
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED)
                throw new SecurityException("请先允许麦克风权限，再点击开始传输");
            Notification notice = notification("正在连接电脑");
            if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION_ID, notice, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
            else startForeground(NOTIFICATION_ID, notice);
        } catch (RuntimeException error) {
            publish(new State(false, false, false, "无法启动麦克风", error instanceof SecurityException
                    ? "请允许麦克风权限，并在打开此页面时点击开始传输" : "无法启动前台服务，请重新打开应用后再试", 0, 0));
            stopForeground(STOP_FOREGROUND_REMOVE);
            stopSelf();
            return START_NOT_STICKY;
        }
        Session session = new Session(host, port);
        current = session;
        publish(new State(true, false, false, "正在连接电脑", "正在等待电脑接受连接，接受后开始采集声音", 0, 0));
        session.worker.start();
        return START_NOT_STICKY; // Never restart microphone capture without another explicit user start.
    }

    private void stopCapture() {
        Session session = current;
        current = null;
        if (session != null) session.cancel();
        publish(new State(false, false, false, "已停止", "麦克风已释放；点击开始可重新连接", 0, 0));
        stopForeground(STOP_FOREGROUND_REMOVE);
        stopSelf();
    }

    @Override public void onDestroy() {
        MAIN.removeCallbacks(watchdog);
        Session session = current;
        current = null;
        if (session != null) {
            session.cancel();
            publish(new State(false, false, false, "已停止", "传输服务已结束，请打开应用重新开始", 0, 0));
        }
        super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }

    private Notification notification(String text) {
        int pendingFlags = PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE;
        PendingIntent open = PendingIntent.getActivity(this, 1, new Intent(this, MainActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), pendingFlags);
        PendingIntent stop = PendingIntent.getService(this, 2, new Intent(this, MicService.class).setAction(STOP), pendingFlags);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_mic)
                .setContentTitle("LanMic · " + text).setContentText("点此返回控制台 · 可随时停止传输")
                .setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true)
                .setCategory(Notification.CATEGORY_SERVICE).setVisibility(Notification.VISIBILITY_PUBLIC)
                .addAction(new Notification.Action.Builder(null, "停止传输", stop).build()).build();
    }

    private void update(final Session session, final String title, final String detail, final boolean connected, final int level) {
        final long frames = session.sentFrames;
        MAIN.post(new Runnable() { @Override public void run() {
            if (current != session || session.cancelled) return;
            boolean changed = !latest.title.equals(title) || latest.muted != session.muted;
            publish(new State(true, connected, session.muted, title, detail, session.muted ? 0 : level, frames));
            if (changed) notifications.notify(NOTIFICATION_ID, notification(session.muted ? "麦克风已静音" : title));
        } });
    }

    private void finished(final Session session, final String error) {
        MAIN.post(new Runnable() { @Override public void run() {
            if (current != session) return;
            current = null;
            publish(new State(false, false, false, error == null ? "已停止" : "传输已停止", error == null ? "麦克风已释放" : error, 0, session.sentFrames));
            stopForeground(STOP_FOREGROUND_REMOVE);
            stopSelf();
        } });
    }

    private static String connectionError(IOException error) {
        if (error instanceof SocketTimeoutException) return "连接超时；检查同一 Wi-Fi、电脑地址以及电脑防火墙是否允许 LanMic";
        if (error instanceof ConnectException) return "无法连接电脑；请确认电脑接收端已开始监听，IP 和端口正确";
        if (error instanceof UnknownHostException) return "找不到电脑地址，请输入电脑显示的 IPv4 地址";
        return "Wi-Fi 连接已中断或电脑停止接收；请检查网络并重新开始";
    }

    private static final class CaptureFailure extends IOException {
        CaptureFailure(String message) { super(message); }
    }

    private final class Session implements Runnable {
        final String host;
        final int port;
        final Thread worker;
        final Object resources = new Object();
        volatile boolean cancelled, muted, watchdogArmed, networkTimedOut;
        volatile long lastNetworkProgress, sentFrames;
        private MicConnection connection;
        private AudioRecord recorder;
        private PowerManager.WakeLock wakeLock;

        Session(String host, int port) {
            this.host = host; this.port = port;
            worker = new Thread(this, "LanMic-audio");
        }

        void cancel() {
            cancelled = true;
            worker.interrupt();
            synchronized (resources) {
                if (connection != null) connection.close();
                stopRecorder();
            }
        }

        void abortStalledNetwork() {
            synchronized (resources) {
                if (!watchdogArmed || cancelled) return;
                networkTimedOut = true;
                watchdogArmed = false;
                if (connection != null) connection.close();
                stopRecorder();
            }
        }

        private void stopRecorder() {
            if (recorder != null) try { recorder.stop(); } catch (IllegalStateException ignored) { }
        }

        @Override public void run() {
            String finalError = null;
            for (int attempt = 0; attempt < 3 && !cancelled; attempt++) {
                boolean retry = false;
                try {
                    networkTimedOut = false;
                    update(this, attempt == 0 ? "正在连接电脑" : "正在重新连接", "正在等待电脑接受连接", false, 0);
                    MicConnection candidate = new MicConnection();
                    synchronized (resources) {
                        if (cancelled) { candidate.close(); break; }
                        connection = candidate;
                    }
                    candidate.connect(host, port, Build.MANUFACTURER + " " + Build.MODEL);
                    if (cancelled) break;
                    lastNetworkProgress = SystemClock.elapsedRealtime();
                    watchdogArmed = true;
                    capture(candidate);
                    if (!cancelled) throw new IOException("Audio stream ended");
                } catch (MicConnection.RejectedException rejected) {
                    finalError = rejected.getMessage(); // Busy and protocol errors never retry.
                } catch (CaptureFailure failure) {
                    finalError = failure.getMessage();
                } catch (SecurityException permission) {
                    finalError = "麦克风权限已关闭；请重新允许权限后点击开始传输";
                } catch (IOException network) {
                    if (!cancelled) { finalError = connectionError(network); retry = attempt < 2; }
                } catch (RuntimeException error) {
                    if (!cancelled) finalError = "无法使用麦克风；请关闭正在占用麦克风的应用后重新开始";
                } finally {
                    releaseAttempt();
                }
                if (!retry || cancelled) break;
                update(this, "连接中断，等待重试", "第 " + (attempt + 1) + " / 2 次重试 · " + finalError, false, 0);
                try { Thread.sleep((attempt + 1) * 1000L); } catch (InterruptedException stopped) { break; }
            }
            finished(this, cancelled ? null : finalError);
        }

        private void capture(MicConnection transport) throws IOException {
            final int samplesPerFrame = 960;
            int minimum = AudioRecord.getMinBufferSize(48000, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (minimum <= 0) throw new CaptureFailure("此手机不支持 48 kHz 单声道 PCM 麦克风采集");
            AudioRecord audio = new AudioRecord(MediaRecorder.AudioSource.MIC, 48000,
                    AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(minimum, MicConnection.FRAME_BYTES * 4));
            synchronized (resources) {
                if (cancelled || networkTimedOut) { audio.release(); return; }
                recorder = audio;
                if (audio.getState() != AudioRecord.STATE_INITIALIZED) throw new CaptureFailure("麦克风初始化失败；请关闭其他录音应用后重试");
                PowerManager power = (PowerManager) getSystemService(POWER_SERVICE);
                wakeLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "LanMic:AudioCapture");
                wakeLock.setReferenceCounted(false);
                wakeLock.acquire(); // Released for every stop, exception and reconnect in releaseAttempt().
                audio.startRecording();
                if (audio.getRecordingState() != AudioRecord.RECORDSTATE_RECORDING) throw new CaptureFailure("麦克风未开始录音，请检查系统麦克风开关");
            }
            short[] samples = new short[samplesPerFrame];
            byte[] pcm = new byte[MicConnection.FRAME_BYTES];
            long lastUi = 0;
            int emptyReads = 0;
            update(this, "正在传输", "48 kHz · 单声道 PCM16", true, 0);
            while (!cancelled) {
                int offset = 0;
                while (offset < samples.length && !cancelled) {
                    if (networkTimedOut) throw new SocketTimeoutException("Audio transmission stalled");
                    int count = audio.read(samples, offset, samples.length - offset, AudioRecord.READ_BLOCKING);
                    if (cancelled) return;
                    if (networkTimedOut) throw new SocketTimeoutException("Audio transmission stalled");
                    if (count < 0) throw new CaptureFailure("麦克风采集被中断；请检查权限、系统麦克风开关或其他录音应用");
                    if (count == 0) {
                        if (++emptyReads >= 3) throw new CaptureFailure("麦克风没有返回音频，请重新连接");
                    } else { emptyReads = 0; offset += count; }
                }
                if (cancelled) return;
                double squares = 0;
                boolean silence = muted;
                for (int i = 0; i < samples.length; i++) {
                    int value = silence ? 0 : samples[i];
                    pcm[i * 2] = (byte) value;
                    pcm[i * 2 + 1] = (byte) (value >> 8);
                    squares += (double) value * value;
                }
                transport.sendPcmFrame(pcm);
                sentFrames++;
                long now = SystemClock.elapsedRealtime();
                lastNetworkProgress = now;
                if (now - lastUi >= 160) {
                    double rms = Math.sqrt(squares / samples.length) / 32768.0;
                    double db = rms > 0 ? 20.0 * Math.log10(rms) : -60;
                    int level = (int) Math.max(0, Math.min(100, (db + 60) * 100 / 60));
                    update(this, "正在传输", "已发送 " + (sentFrames / 50) + " 秒音频", true, level);
                    lastUi = now;
                }
            }
        }

        private void releaseAttempt() {
            watchdogArmed = false;
            synchronized (resources) {
                if (connection != null) { connection.close(); connection = null; }
                try {
                    if (recorder != null) {
                        stopRecorder();
                        try { recorder.release(); } catch (RuntimeException ignored) { }
                        recorder = null;
                    }
                } finally {
                    if (wakeLock != null) {
                        try { if (wakeLock.isHeld()) wakeLock.release(); } catch (RuntimeException ignored) { }
                        wakeLock = null;
                    }
                }
            }
        }
    }
}
