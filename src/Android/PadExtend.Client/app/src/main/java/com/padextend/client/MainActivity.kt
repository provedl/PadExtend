package com.padextend.client

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Gravity
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import java.io.DataInputStream
import java.io.EOFException
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import kotlin.concurrent.thread

// PadExtend 接收端（阶段四：H.264 硬解 + SurfaceView 实时显示）
// 通过 adb reverse 的 USB 通道连接 PC 的 H.264 发送端，按协议 v1.1 解析：
//   type=1 文本, type=3 SPS/PPS(csd-0), type=4 IDR, type=5 P 帧, type=6 元信息(JSON)
// 用 MediaCodec(video/avc) 硬件解码，输出直接渲染到 SurfaceView。断线自动重连。
class MainActivity : AppCompatActivity(), SurfaceHolder.Callback {

    private companion object {
        const val HOST = "127.0.0.1"
        const val PORT = 27555
        const val MAGIC = 0x50455831 // "PEX1"
        const val TYPE_TEXT = 1
        const val TYPE_JPEG = 2
        const val TYPE_CONFIG = 3
        const val TYPE_KEYFRAME = 4
        const val TYPE_DELTA = 5
        const val TYPE_META = 6
        const val MAX_PAYLOAD = 16 * 1024 * 1024
        const val TAG = "PadExtendRx"
        const val RECONNECT_MIN_MS = 150L
        const val RECONNECT_MAX_MS = 1500L
    }

    private lateinit var statusView: TextView
    private lateinit var surfaceView: SurfaceView
    private val ui = Handler(Looper.getMainLooper())

    @Volatile private var running = true
    @Volatile private var surfaceReady = false
    // 当前连接的 socket：surface 销毁（锁屏/切后台）时关闭它，唤醒阻塞的读，走重连恢复路径。
    @Volatile private var currentSocket: Socket? = null

    private var codec: MediaCodec? = null
    private var codecConfigured = false
    // 刚建好的解码器在收到首个 IDR 前为 true：丢弃此前的 P 帧，避免无参考帧导致花屏/卡死。
    @Volatile private var awaitingKeyframe = true
    private var pendingCsd: ByteArray? = null
    private var videoW = 0
    private var videoH = 0
    private var videoFps = 30

    private var framesDecoded = 0L
    private var seqGaps = 0L
    private var lastSeq = -1L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 保持屏幕常亮：扩展屏显示时不应息屏。
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        // 内容延伸到系统栏/刘海区，交由我们自己铺满。
        WindowCompat.setDecorFitsSystemWindows(window, false)

        // 根布局用 FrameLayout：SurfaceView 铺满，状态文本浮在其上，不再占画面高度。
        val root = FrameLayout(this)
        surfaceView = SurfaceView(this).apply {
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            )
        }
        statusView = TextView(this).apply {
            textSize = 12f
            setTextColor(0xB0FFFFFF.toInt())
            setBackgroundColor(0x40000000)
            setPadding(16, 8, 16, 8)
            text = "status: starting..."
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP or Gravity.START
            )
        }
        surfaceView.holder.addCallback(this)
        root.addView(surfaceView)
        root.addView(statusView)
        setContentView(root)
        enableImmersiveMode()
    }

    // 隐藏系统状态栏 + 导航栏，进入沉浸式；边缘上滑可临时唤出，随后自动隐藏。
    private fun enableImmersiveMode() {
        val controller = WindowInsetsControllerCompat(window, window.decorView)
        controller.hide(WindowInsetsCompat.Type.systemBars())
        controller.systemBarsBehavior =
            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // 对话框/通知抢焦后重新回到应用时，重新进入沉浸式。
        if (hasFocus) enableImmersiveMode()
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        surfaceReady = true
        startReceiverLoop()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surfaceReady = false
        releaseCodec()
        // 关闭当前连接，唤醒阻塞在读的接收线程；surface 恢复后会自动重连并在新 surface 上重建解码器。
        try { currentSocket?.close() } catch (_: Exception) {}
    }

    override fun onDestroy() {
        running = false
        releaseCodec()
        super.onDestroy()
    }

    private var receiverStarted = false
    private fun startReceiverLoop() {
        if (receiverStarted) return
        receiverStarted = true
        thread(name = "receiver") {
            // 快速退避：拔插/断线后尽快重连，连续失败再逐步拉长，避免空转。
            var backoffMs = RECONNECT_MIN_MS
            while (running) {
                // surface 未就绪（锁屏/切后台）时不发起连接，避免空转；等 surface 回来再连。
                if (!surfaceReady) {
                    Thread.sleep(100)
                    continue
                }
                try {
                    connectAndReceive()
                    backoffMs = RECONNECT_MIN_MS // 正常断开（服务端换人等）后也快速重试
                } catch (e: Exception) {
                    // surface 没了导致的断开是预期行为，不当错误刷屏。
                    if (surfaceReady) setStatus("disconnected: ${e.message}, retrying...")
                    Log.w(TAG, "disconnected", e)
                    releaseCodec()
                }
                if (!running) break
                if (surfaceReady) {
                    Thread.sleep(backoffMs)
                    backoffMs = (backoffMs * 2).coerceAtMost(RECONNECT_MAX_MS)
                } else {
                    backoffMs = RECONNECT_MIN_MS // surface 回来后立即重连
                }
            }
        }
    }

    private fun connectAndReceive() {
        setStatus("connecting $HOST:$PORT ...")
        Socket().use { socket ->
            currentSocket = socket
            try {
                socket.tcpNoDelay = true
                socket.connect(InetSocketAddress(HOST, PORT), 3000)
                setStatus("connected $HOST:$PORT")
                lastSeq = -1
                // 新连接必须从干净状态开始：释放上一条连接遗留的解码器，
                // 否则 ensureCodec() 会因 codecConfigured/codec 残留而复用绑定到旧 surface 的实例，
                // 在部分机型上导致新建解码器失败 -> 重开后永久黑屏。
                releaseCodec()
                pendingCsd = null
                // 重置视频参数，等待新的 meta 消息
                videoW = 0
                videoH = 0
                val input = DataInputStream(socket.getInputStream().buffered(1 shl 16))
                val header = ByteArray(16)
                
                // 超时检测：如果连接后长时间没收到有效视频帧，可能服务端状态异常，主动断开重连
                var lastValidFrameTime = System.currentTimeMillis()
                val noFrameTimeoutMs = 10000L // 10 秒无有效帧则重连
                
                while (running && surfaceReady) {
                    // 设置 socket 读超时，避免永久阻塞
                    socket.soTimeout = 5000
                    try {
                        readFully(input, header, 16)
                    } catch (e: java.net.SocketTimeoutException) {
                        // 读超时：检查是否长时间没有有效帧
                        val now = System.currentTimeMillis()
                        if (now - lastValidFrameTime > noFrameTimeoutMs) {
                            Log.w(TAG, "no valid frames for ${noFrameTimeoutMs}ms, reconnecting...")
                            setStatus("timeout: no frames, reconnecting...")
                            throw e
                        }
                        continue // 继续等待
                    }
                    
                    if (beInt(header, 0) != MAGIC) throw IllegalStateException("bad magic")
                    val seq = beInt(header, 4).toLong() and 0xFFFFFFFFL
                    val type = beInt(header, 8)
                    val length = beInt(header, 12)
                    if (length < 0 || length > MAX_PAYLOAD) throw IllegalStateException("bad length $length")
                    val payload = ByteArray(length)
                    readFully(input, payload, length)

                    if (lastSeq >= 0 && seq != lastSeq + 1) seqGaps++
                    lastSeq = seq

                    when (type) {
                        TYPE_TEXT -> Log.i(TAG, "TEXT: ${String(payload)}")
                        TYPE_META -> handleMeta(String(payload))
                        TYPE_CONFIG -> {
                            pendingCsd = payload
                            ensureCodec()
                        }
                        TYPE_KEYFRAME, TYPE_DELTA -> {
                            feedFrame(payload, type == TYPE_KEYFRAME)
                            // 用“真正解码出画面”作为健康信号：只在解码器已产出过帧时才续租超时。
                            // 若只是收到帧但一直在等 IDR（awaitingKeyframe）或解码器卡住不出画面，
                            // 则不续租，让 noFrameTimeout 到点后主动断开重连，避免永久黑屏。
                            if (codecConfigured && !awaitingKeyframe && framesDecoded > 0) {
                                lastValidFrameTime = System.currentTimeMillis()
                            }
                        }
                        else -> Log.i(TAG, "ignore type=$type len=$length")
                    }
                }
            } finally {
                currentSocket = null
            }
        }
    }

    private fun handleMeta(json: String) {
        // 极简解析 {"w":..,"h":..,"fps":..}，避免引入 JSON 库。
        videoW = extractInt(json, "w") ?: videoW
        videoH = extractInt(json, "h") ?: videoH
        videoFps = extractInt(json, "fps") ?: videoFps
        Log.i(TAG, "meta w=$videoW h=$videoH fps=$videoFps")
        // 收到 meta 后，如果已有 csd 但还没建解码器，现在可以建了
        if (!codecConfigured && pendingCsd != null) {
            ensureCodec()
        }
    }

    private fun extractInt(s: String, key: String): Int? {
        val re = Regex("\"$key\"\\s*:\\s*(\\d+)")
        return re.find(s)?.groupValues?.get(1)?.toIntOrNull()
    }

    private fun ensureCodec() {
        if (codecConfigured) return
        val csd = pendingCsd ?: return
        if (!surfaceReady) return
        // 分辨率优先用 meta；但 meta 可能在重连时丢失/错序，而 config 每连接只发一次，
        // 不能因为缺 meta 就永远不建解码器（否则永久黑屏）。SPS(csd-0) 本身带有真实尺寸，
        // createVideoFormat 的宽高只是提示，解码器会按码流自行修正——故缺 meta 时退回默认值兜底。
        val w = if (videoW > 0) videoW else 1920
        val h = if (videoH > 0) videoH else 1080
        try {
            val mc = MediaCodec.createDecoderByType("video/avc")
            val fmt = MediaFormat.createVideoFormat("video/avc", w, h)
            fmt.setByteBuffer("csd-0", ByteBuffer.wrap(csd))
            mc.configure(fmt, surfaceView.holder.surface, null, 0)
            mc.start()
            codec = mc
            codecConfigured = true
            awaitingKeyframe = true
            setStatus("decoder started ${w}x${h}@${videoFps}")
            Log.i(TAG, "decoder configured ${w}x$h")
        } catch (e: Exception) {
            Log.e(TAG, "codec configure failed", e)
            setStatus("codec error: ${e.message}")
        }
    }

    private fun feedFrame(data: ByteArray, isKey: Boolean) {
        if (!surfaceReady) return
        val mc = codec ?: run { ensureCodec(); codec } ?: return
        if (!codecConfigured) return
        // 新解码器尚无参考帧时丢弃 P 帧：无参考的 P 帧会解出花屏/黑帧，
        // 部分机型还会卡住输出。等到第一个 IDR 再开始喂数据。
        if (awaitingKeyframe) {
            if (!isKey) return
            awaitingKeyframe = false
        }
        try {
            // 先排空输出，既渲染已解码帧，也及时归还输入缓冲，避免输入槽长期占满丢掉关键帧。
            drainOutput(mc)
            // 关键帧必须喂进去（否则永远出不了画面），给它更长的等待；P 帧拿不到槽就丢。
            val waitUs = if (isKey) 100000L else 2000L
            val inIdx = mc.dequeueInputBuffer(waitUs)
            if (inIdx >= 0) {
                val buf = mc.getInputBuffer(inIdx)!!
                buf.clear()
                buf.put(data)
                val flags = if (isKey) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
                mc.queueInputBuffer(inIdx, 0, data.size, System.nanoTime() / 1000, flags)
            } else if (isKey) {
                // 关键帧都塞不进去说明解码器已阻塞，重建以求恢复画面。
                Log.w(TAG, "no input buffer for keyframe, resetting decoder")
                releaseCodec()
                return
            }
            drainOutput(mc)
            if (framesDecoded % 30 == 0L) {
                setStatus("playing ${videoW}x${videoH} | frames=$framesDecoded gaps=$seqGaps")
            }
        } catch (e: Exception) {
            Log.e(TAG, "decode error", e)
            setStatus("decode error: ${e.message}")
            releaseCodec()
        }
    }

    // 排空解码器输出队列并渲染。INFO_TRY_AGAIN_LATER(-1)/OUTPUT_FORMAT_CHANGED(-2) 等负值自然结束循环。
    private fun drainOutput(mc: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        var outIdx = mc.dequeueOutputBuffer(info, 0)
        while (outIdx >= 0) {
            mc.releaseOutputBuffer(outIdx, surfaceReady) // 仅在 surface 就绪时渲染
            framesDecoded++
            outIdx = mc.dequeueOutputBuffer(info, 0)
        }
    }

    private fun releaseCodec() {
        try { codec?.stop() } catch (_: Exception) {}
        try { codec?.release() } catch (_: Exception) {}
        codec = null
        codecConfigured = false
    }

    private fun readFully(input: DataInputStream, buf: ByteArray, len: Int) {
        var off = 0
        while (off < len) {
            val n = input.read(buf, off, len - off)
            if (n < 0) throw EOFException("closed at $off/$len")
            off += n
        }
    }

    private fun beInt(b: ByteArray, off: Int): Int =
        ((b[off].toInt() and 0xFF) shl 24) or
        ((b[off + 1].toInt() and 0xFF) shl 16) or
        ((b[off + 2].toInt() and 0xFF) shl 8) or
        (b[off + 3].toInt() and 0xFF)

    private fun setStatus(s: String) = ui.post {
        statusView.text = "status: $s"
        Log.i(TAG, s)
    }
}


