package io.lanmic.app;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.util.ArrayList;

/** Platform-only Android UI; microphone capture is owned by MicService. */
public final class MainActivity extends Activity implements MicService.Observer {
    private static final int BG = Color.rgb(11, 18, 32);
    private static final int CARD = Color.rgb(19, 31, 50);
    private static final int TEXT = Color.rgb(239, 246, 255);
    private static final int SECONDARY = Color.rgb(154, 173, 195);
    private static final int ACCENT = Color.rgb(45, 212, 191);
    private static final int REQUEST_PERMISSIONS = 21;
    private EditText host, port, pairing;
    private TextView statusTitle, statusDetail, statusBadge, levelText;
    private Button start, mute;
    private LevelMeter meter;
    private SharedPreferences preferences;
    private boolean resumed, awaitingPermission, startAfterPermission;

    @Override public void onCreate(Bundle savedState) {
        super.onCreate(savedState);
        preferences = getSharedPreferences("connection", MODE_PRIVATE);
        buildUi();
        host.setText(preferences.getString("host", ""));
        port.setText(Integer.toString(preferences.getInt("port", 18765)));
        onMicState(MicService.state());
    }

    private void buildUi() {
        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(BG);
        scroll.setFillViewport(true);
        LinearLayout page = column();
        page.setPadding(dp(24), dp(28), dp(24), dp(28));
        scroll.addView(page);
        setContentView(scroll);

        TextView eyebrow = text("手机 → 电脑 · WI-FI 音频", 11, ACCENT);
        eyebrow.setLetterSpacing(0.13f);
        page.addView(eyebrow);
        TextView title = text("LanMic", 38, TEXT);
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        add(page, title, 8);
        add(page, text("把安卓手机，变成电脑麦克风。", 15, SECONDARY), 4);

        LinearLayout live = card();
        add(page, live, 28);
        statusBadge = text("●  尚未连接", 11, SECONDARY);
        live.addView(statusBadge);
        statusTitle = text("准备连接", 23, TEXT);
        statusTitle.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        add(live, statusTitle, 10);
        statusDetail = text("先在电脑上启动 LanMic 接收端", 13, SECONDARY);
        statusDetail.setMinLines(2);
        add(live, statusDetail, 7);
        LinearLayout labels = new LinearLayout(this);
        labels.setGravity(Gravity.CENTER_VERTICAL);
        labels.addView(text("麦克风电平", 11, SECONDARY), new LinearLayout.LayoutParams(0, -2, 1));
        levelText = text("等待音频", 11, SECONDARY);
        labels.addView(levelText);
        add(live, labels, 18);
        meter = new LevelMeter();
        LinearLayout.LayoutParams meterSize = new LinearLayout.LayoutParams(-1, dp(24));
        meterSize.topMargin = dp(8);
        live.addView(meter, meterSize);

        LinearLayout connectionCard = card();
        add(page, connectionCard, 16);
        TextView settingsTitle = text("连接到电脑", 17, TEXT);
        settingsTitle.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        connectionCard.addView(settingsTitle);
        add(connectionCard, text("两台设备需要接入同一局域网", 12, SECONDARY), 5);

        LinearLayout addressRow = new LinearLayout(this);
        addressRow.setBaselineAligned(false);
        LinearLayout addressBox = column();
        addressBox.addView(text("电脑 IPv4 地址", 12, SECONDARY));
        host = field("例如 192.168.1.8", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        add(addressBox, host, 7);
        addressRow.addView(addressBox, new LinearLayout.LayoutParams(0, -2, 1));
        LinearLayout portBox = column();
        portBox.addView(text("端口", 12, SECONDARY));
        port = field("18765", InputType.TYPE_CLASS_NUMBER);
        add(portBox, port, 7);
        LinearLayout.LayoutParams portWidth = new LinearLayout.LayoutParams(dp(92), -2);
        portWidth.leftMargin = dp(12);
        addressRow.addView(portBox, portWidth);
        add(connectionCard, addressRow, 22);

        add(connectionCard, text("电脑上显示的完整配对码", 12, SECONDARY), 18);
        pairing = field("ABCD-EF01-2345-6789-ABCD-123456", InputType.TYPE_CLASS_TEXT
                | InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        pairing.setSingleLine(false);
        pairing.setMinLines(2);
        pairing.setMaxLines(3);
        pairing.setTextSize(14);
        pairing.setTypeface(Typeface.MONOSPACE);
        pairing.setSaveEnabled(false);
        pairing.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        add(connectionCard, pairing, 7);
        add(connectionCard, text("配对码每次电脑开始监听时更新，不会保存。", 11, SECONDARY), 8);

        start = button("开始传输", ACCENT, BG);
        LinearLayout.LayoutParams startSize = new LinearLayout.LayoutParams(-1, dp(58));
        startSize.topMargin = dp(20);
        page.addView(start, startSize);
        start.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View view) {
            if (MicService.state().running) {
                startService(new Intent(MainActivity.this, MicService.class).setAction(MicService.STOP));
            } else requestStart();
        } });
        mute = button("静音", CARD, TEXT);
        LinearLayout.LayoutParams muteSize = new LinearLayout.LayoutParams(-1, dp(50));
        muteSize.topMargin = dp(10);
        page.addView(mute, muteSize);
        mute.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View view) {
            MicService.State state = MicService.state();
            if (state.running) startService(new Intent(MainActivity.this, MicService.class)
                    .setAction(MicService.MUTE).putExtra("muted", !state.muted));
        } });

        add(page, text("首次连接", 13, TEXT), 26);
        add(page, text("1  电脑选择音频输出，点击「开始监听」。\n2  填入电脑显示的 IP、端口和完整配对码。\n3  点击「开始传输」，允许使用麦克风。", 12, SECONDARY), 9);
        TextView privacy = text("声音通过 TLS 加密传输，不录音存盘。锁屏后可继续传输，可随时在通知中停止。静音时保持连接并发送静音音频。", 11, SECONDARY);
        add(page, privacy, 22);
        add(page, text("用于会议或游戏时，电脑需要配置虚拟音频设备；请参阅电脑端说明。", 11, SECONDARY), 10);
    }

    private void requestStart() {
        if (!validateInputs()) return;
        ArrayList<String> permissions = new ArrayList<String>();
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED)
            permissions.add(Manifest.permission.RECORD_AUDIO);
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
                && !preferences.getBoolean("notification_permission_requested", false)) {
            permissions.add(Manifest.permission.POST_NOTIFICATIONS);
            preferences.edit().putBoolean("notification_permission_requested", true).apply();
        }
        if (!permissions.isEmpty()) {
            awaitingPermission = true;
            requestPermissions(permissions.toArray(new String[permissions.size()]), REQUEST_PERMISSIONS);
        } else launchCapture();
    }

    private boolean validateInputs() {
        String address = host.getText().toString().trim();
        String[] octets = address.split("\\.", -1);
        boolean validAddress = octets.length == 4;
        for (String octet : octets) {
            if (!octet.matches("[0-9]{1,3}")) { validAddress = false; break; }
            int number = Integer.parseInt(octet);
            if (number > 255 || (octet.length() > 1 && octet.charAt(0) == '0')) { validAddress = false; break; }
        }
        if (!validAddress || "0.0.0.0".equals(address) || "255.255.255.255".equals(address)) {
            host.setError("请输入电脑显示的 IPv4 地址"); host.requestFocus(); return false;
        }
        try {
            int value = Integer.parseInt(port.getText().toString().trim());
            if (value < 1 || value > 65535) throw new NumberFormatException();
        } catch (NumberFormatException error) {
            port.setError("端口范围是 1–65535"); port.requestFocus(); return false;
        }
        try { MicConnection.normalizePairingToken(pairing.getText().toString()); }
        catch (IllegalArgumentException error) { pairing.setError(error.getMessage()); pairing.requestFocus(); return false; }
        return true;
    }

    private void launchCapture() {
        if (!resumed || MicService.state().running || !validateInputs()) return;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return;
        String address = host.getText().toString().trim();
        int portNumber = Integer.parseInt(port.getText().toString().trim());
        preferences.edit().putString("host", address).putInt("port", portNumber).apply();
        InputMethodManager keyboard = (InputMethodManager) getSystemService(INPUT_METHOD_SERVICE);
        keyboard.hideSoftInputFromWindow(pairing.getWindowToken(), 0);
        try {
            start.setEnabled(false);
            statusTitle.setText("正在启动");
            statusDetail.setText("正在启动麦克风前台服务");
            startForegroundService(new Intent(this, MicService.class).setAction(MicService.START)
                    .putExtra("host", address).putExtra("port", portNumber)
                    .putExtra("token", MicConnection.normalizePairingToken(pairing.getText().toString())));
        } catch (RuntimeException error) {
            start.setEnabled(true);
            statusTitle.setText("无法启动");
            statusDetail.setText("请允许麦克风权限，并在此页面点击开始传输。");
        }
    }

    @Override public void onRequestPermissionsResult(int code, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(code, permissions, results);
        if (code != REQUEST_PERMISSIONS || !awaitingPermission) return;
        awaitingPermission = false;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            if (resumed) launchCapture();
            else startAfterPermission = true;
        } else {
            statusTitle.setText("需要麦克风权限");
            statusDetail.setText("允许录音权限后，才能把手机声音传输到电脑。");
            new AlertDialog.Builder(this).setTitle("允许 LanMic 使用麦克风")
                    .setMessage("请在应用权限中允许「麦克风」。只有你点击开始传输后才会采集声音。")
                    .setPositiveButton("打开应用设置", (dialog, which) -> startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                            Uri.parse("package:" + getPackageName()))))
                    .setNegativeButton("暂不", null).show();
        }
    }

    @Override protected void onStart() { super.onStart(); MicService.observe(this); }
    @Override protected void onStop() { MicService.unobserve(this); super.onStop(); }
    @Override protected void onResume() {
        super.onResume(); resumed = true;
        if (startAfterPermission) { startAfterPermission = false; launchCapture(); }
    }
    @Override protected void onPause() { resumed = false; super.onPause(); }

    @Override public void onMicState(MicService.State state) {
        statusTitle.setText(state.connected && state.muted ? "已静音，连接保持中" : state.title);
        statusDetail.setText(state.detail);
        statusBadge.setText(state.connected ? "●  加密连接已建立" : state.running ? "●  正在连接" : "●  尚未连接");
        statusBadge.setTextColor(state.connected ? ACCENT : state.running ? Color.rgb(251, 191, 36) : SECONDARY);
        meter.setLevel(state.level);
        levelText.setText(state.muted ? "静音" : state.connected ? "48 kHz / 单声道" : "等待音频");
        start.setEnabled(true);
        start.setText(state.running ? "停止传输" : "开始传输");
        start.setBackgroundTintList(ColorStateList.valueOf(state.running ? Color.rgb(251, 113, 133) : ACCENT));
        mute.setEnabled(state.running);
        mute.setAlpha(state.running ? 1f : 0.45f);
        mute.setText(state.muted ? "取消静音" : "静音");
        host.setEnabled(!state.running); port.setEnabled(!state.running); pairing.setEnabled(!state.running);
    }

    private LinearLayout column() { LinearLayout value = new LinearLayout(this); value.setOrientation(LinearLayout.VERTICAL); return value; }
    private LinearLayout card() {
        LinearLayout value = column(); value.setPadding(dp(18), dp(20), dp(18), dp(20)); value.setBackground(shape(CARD, 18)); return value;
    }
    private TextView text(String value, int size, int color) {
        TextView text = new TextView(this); text.setText(value); text.setTextSize(size); text.setTextColor(color);
        text.setLineSpacing(dp(3), 1); text.setIncludeFontPadding(false); return text;
    }
    private EditText field(String hint, int inputType) {
        EditText value = new EditText(this); value.setTextSize(15); value.setTextColor(TEXT); value.setHintTextColor(Color.rgb(109, 130, 155));
        value.setHint(hint); value.setInputType(inputType); value.setSingleLine(true); value.setPadding(dp(12), dp(12), dp(12), dp(12));
        GradientDrawable background = shape(BG, 10); background.setStroke(dp(1), Color.rgb(40, 59, 82)); value.setBackground(background);
        value.setMinHeight(dp(50)); value.setSelectAllOnFocus(false); return value;
    }
    private Button button(String title, int background, int foreground) {
        Button value = new Button(this); value.setText(title); value.setTextSize(16); value.setTextColor(foreground);
        value.setAllCaps(false); value.setTypeface(Typeface.DEFAULT, Typeface.BOLD); value.setBackground(shape(background, 12));
        value.setElevation(0); return value;
    }
    private GradientDrawable shape(int color, int radius) { GradientDrawable shape = new GradientDrawable(); shape.setColor(color); shape.setCornerRadius(dp(radius)); return shape; }
    private void add(LinearLayout parent, View child, int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2); params.topMargin = dp(top); parent.addView(child, params);
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }

    private final class LevelMeter extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private int level;
        LevelMeter() { super(MainActivity.this); setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_YES); setContentDescription("麦克风电平 0%"); }
        void setLevel(int value) { level = value; setContentDescription("麦克风电平 " + value + "%"); invalidate(); }
        @Override protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            int bars = 28;
            float gap = dp(3), width = (getWidth() - gap * (bars - 1)) / bars;
            int lit = Math.round(level * bars / 100f);
            for (int i = 0; i < bars; i++) {
                paint.setColor(i < lit ? i >= 24 ? Color.rgb(251, 191, 36) : ACCENT : Color.rgb(37, 55, 77));
                float x = i * (width + gap);
                canvas.drawRoundRect(x, dp(3), x + width, getHeight() - dp(3), dp(2), dp(2), paint);
            }
        }
    }
}
