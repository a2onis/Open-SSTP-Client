package kittoku.osc.service

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ComponentName
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.service.quicksettings.TileService
import androidx.core.app.ActivityCompat
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.documentfile.provider.DocumentFile
import androidx.preference.PreferenceManager
import kittoku.osc.R
import kittoku.osc.SharedBridge
import kittoku.osc.activity.MainActivity
import kittoku.osc.control.Controller
import kittoku.osc.control.LogWriter
import kittoku.osc.control.SmartConnect
import kittoku.osc.control.SmartState
import kittoku.osc.preference.OscPrefKey
import kittoku.osc.preference.accessor.getBooleanPrefValue
import kittoku.osc.preference.accessor.getIntPrefValue
import kittoku.osc.preference.accessor.getURIPrefValue
import kittoku.osc.preference.accessor.resetReconnectionLife
import kittoku.osc.preference.accessor.setBooleanPrefValue
import kittoku.osc.preference.accessor.setIntPrefValue
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale


internal const val ACTION_VPN_CONNECT = "kittoku.osc.connect"
internal const val ACTION_VPN_DISCONNECT = "kittoku.osc.disconnect"

internal const val NOTIFICATION_ERROR_CHANNEL = "ERROR"
internal const val NOTIFICATION_RECONNECT_CHANNEL = "RECONNECT"
internal const val NOTIFICATION_DISCONNECT_CHANNEL = "DISCONNECT"
internal const val NOTIFICATION_CERTIFICATE_CHANNEL = "CERTIFICATE"

internal const val NOTIFICATION_ERROR_ID = 1
internal const val NOTIFICATION_RECONNECT_ID = 2
internal const val NOTIFICATION_DISCONNECT_ID = 3
internal const val NOTIFICATION_CERTIFICATE_ID = 4


internal class SstpVpnService : VpnService() {
    private lateinit var prefs: SharedPreferences
    private lateinit var listener: SharedPreferences.OnSharedPreferenceChangeListener
    private lateinit var notificationManager: NotificationManagerCompat
    internal lateinit var scope: CoroutineScope

    internal var logWriter: LogWriter? = null
    private var controller: Controller?  = null

    private var jobReconnect: Job? = null

    private fun setRootState(state: Boolean) {
        setBooleanPrefValue(state, OscPrefKey.ROOT_STATE, prefs)
    }

    private fun requestTileListening() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            TileService.requestListeningState(this,
                ComponentName(this, SstpTileService::class.java)
            )
        }
    }

    private val connectorListener = SharedPreferences.OnSharedPreferenceChangeListener { _, key ->
        if (key == OscPrefKey.ROOT_STATE.name) {
            val newState = getBooleanPrefValue(OscPrefKey.ROOT_STATE, prefs)

            setBooleanPrefValue(newState, OscPrefKey.HOME_CONNECTOR, prefs)
            requestTileListening()
            refreshForeground()
        }
    }

    override fun onCreate() {
        notificationManager = NotificationManagerCompat.from(this)

        prefs = PreferenceManager.getDefaultSharedPreferences(this)
        listener = connectorListener
        prefs.registerOnSharedPreferenceChangeListener(listener)

        scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        return when (intent?.action) {
            ACTION_VPN_CONNECT -> {
                // A retry loop from the previous session must stop before the new one
                // replaces its settings.
                runBlocking { jobReconnect?.cancelAndJoin() }
                controller?.kill(false, null)

                beForegrounded()

                resetReconnectionLife(prefs)

                // Detect the path MTU, replay the strategy that worked last time and
                // reset the retry ladder before the PPP stack reads the settings.
                SmartConnect.reserveForConnect(this, prefs)

                if (getBooleanPrefValue(OscPrefKey.LOG_DO_SAVE_LOG, prefs)) {
                    prepareLogWriter()
                }

                logWriter?.write("Establish VPN connection")

                initializeClient()

                setRootState(true)

                START_STICKY
            }

            else -> {
                // ensure that reconnection has been completely canceled or done
                runBlocking { jobReconnect?.cancelAndJoin() }

                controller?.disconnect()
                controller = null

                close()

                START_NOT_STICKY
            }
        }
    }

    private fun initializeClient() {
        controller = Controller(SharedBridge(this)).also {
            it.launchJobMain()
        }

        refreshForeground()
    }

    private fun prepareLogWriter() {
        val currentDateTime = SimpleDateFormat("yyyyMMddHHmmss", Locale.getDefault()).format(Date())
        val filename = "log_osc_${currentDateTime}.txt"

        val prefURI = getURIPrefValue(OscPrefKey.LOG_DIR, prefs)
        if (prefURI == null) {
            notifyError("LOG: ERR_NULL_PREFERENCE")
            return
        }

        val dirURI = DocumentFile.fromTreeUri(this, prefURI)
        if (dirURI == null) {
            notifyError("LOG: ERR_NULL_DIRECTORY")
            return
        }

        val fileURI = dirURI.createFile("text/plain", filename)
        if (fileURI == null) {
            notifyError("LOG: ERR_NULL_FILE")
            return
        }

        val stream = contentResolver.openOutputStream(fileURI.uri, "wa")
        if (stream == null) {
            notifyError("LOG: ERR_NULL_STREAM")
            return
        }

        logWriter = LogWriter(stream)
    }

    internal fun launchJobReconnect() {
        jobReconnect = scope.launch {
            // A newer connect request starts a new session; an older retry loop must
            // not touch its settings or start a second controller.
            val token = SmartConnect.sessionToken(prefs)

            try {
                if (SmartConnect.isFallbackEnabled(prefs)) {
                    val plan = SmartConnect.nextPlan(this@SstpVpnService, prefs)

                    if (plan == null) {
                        logWriter?.report("Smart connect exhausted every attempt")
                        notifyError("CONNECTION FAILED")
                        close()
                        return@launch
                    }

                    val message = "Attempt ${plan.attempt}/${plan.totalAttempts}: ${plan.detail} (in ${plan.delayMs / 1000}s)"
                    notifyMessage(message, NOTIFICATION_RECONNECT_ID, NOTIFICATION_RECONNECT_CHANNEL)
                    logWriter?.report(message)
                    refreshForeground()

                    delay(plan.delayMs)

                    if (SmartConnect.sessionToken(prefs) != token) {
                        return@launch
                    }

                    initializeClient()
                    return@launch
                }

                val life = getIntPrefValue(OscPrefKey.RECONNECTION_LIFE, prefs) - 1
                setIntPrefValue(life, OscPrefKey.RECONNECTION_LIFE, prefs)
                val message = "Reconnection will be tried (LIFE = $life)"
                notifyMessage(message, NOTIFICATION_RECONNECT_ID, NOTIFICATION_RECONNECT_CHANNEL)
                logWriter?.report(message)

                delay(getIntPrefValue(OscPrefKey.RECONNECTION_INTERVAL, prefs) * 1000L)

                if (SmartConnect.sessionToken(prefs) != token) {
                    return@launch
                }

                initializeClient()
            } catch (_: CancellationException) { }
            finally {
                cancelNotification(NOTIFICATION_RECONNECT_ID)
            }
        }
    }

    private fun createChannels() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val tunnel = NotificationChannel(
                NOTIFICATION_DISCONNECT_CHANNEL,
                "VPN connection",
                NotificationManager.IMPORTANCE_LOW
            ).also {
                it.description = "Shows the tunnel state and offers disconnect"
                it.setShowBadge(false)
            }

            listOf(
                NotificationChannel(NOTIFICATION_ERROR_CHANNEL, "Errors", NotificationManager.IMPORTANCE_DEFAULT),
                NotificationChannel(NOTIFICATION_RECONNECT_CHANNEL, "Reconnection", NotificationManager.IMPORTANCE_LOW),
                tunnel,
                NotificationChannel(NOTIFICATION_CERTIFICATE_CHANNEL, "Certificates", NotificationManager.IMPORTANCE_DEFAULT),
            ).also {
                notificationManager.createNotificationChannels(it)
            }
        }
    }

    private fun contentText(): String {
        val endpoint = SmartConnect.endpoint(prefs)

        return when (SmartConnect.state(prefs)) {
            SmartState.CONNECTED -> {
                val mtu = SmartConnect.activeMtu(prefs)
                "Connected · $endpoint · MTU $mtu"
            }

            SmartState.CONNECTING -> "Connecting · $endpoint"
            SmartState.RETRYING -> SmartConnect.detail(prefs).ifBlank { "Reconnecting · $endpoint" }
            SmartState.FAILED -> SmartConnect.detail(prefs).ifBlank { "Connection failed" }
            SmartState.IDLE -> "Disconnected"
        }
    }

    private fun buildNotification(): Notification {
        val disconnectIntent = PendingIntent.getService(
            this,
            0,
            Intent(this, SstpVpnService::class.java).setAction(ACTION_VPN_DISCONNECT),
            PendingIntent.FLAG_ONE_SHOT or PendingIntent.FLAG_IMMUTABLE
        )

        val openAppIntent = PendingIntent.getActivity(
            this,
            1,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(this, NOTIFICATION_DISCONNECT_CHANNEL).also {
            it.priority = NotificationCompat.PRIORITY_LOW
            it.setOngoing(true)
            it.setAutoCancel(true)
            it.setOnlyAlertOnce(true)
            it.setShowWhen(false)
            it.setSmallIcon(R.drawable.ic_baseline_vpn_lock_24)
            it.setContentTitle(getString(R.string.app_name))
            it.setContentText(contentText())
            it.setContentIntent(openAppIntent)
            it.setCategory(NotificationCompat.CATEGORY_SERVICE)
            it.addAction(R.drawable.ic_baseline_close_24, "DISCONNECT", disconnectIntent)
        }.build()
    }

    private fun beForegrounded() {
        createChannels()
        startForeground(NOTIFICATION_DISCONNECT_ID, buildNotification())
    }

    /** Re-renders the ongoing notification after the tunnel state changed. */
    internal fun refreshForeground() {
        if (!::prefs.isInitialized) return

        try {
            startForeground(NOTIFICATION_DISCONNECT_ID, buildNotification())
        } catch (_: Exception) {
            // The service may not be in the foreground yet; the next refresh fixes it.
        }
    }

    internal fun onConnected() {
        refreshForeground()

        val endpoint = SmartConnect.endpoint(prefs)
        val message = "Connected to $endpoint (MTU ${SmartConnect.activeMtu(prefs)})"
        scope.launch { logWriter?.report(message) }
    }

    internal fun notifyMessage(message: String, id: Int, channel: String) {
        NotificationCompat.Builder(this, channel).also {
            it.setSmallIcon(R.drawable.ic_baseline_vpn_lock_24)
            it.setContentTitle(getString(R.string.app_name))
            it.setContentText(message)
            it.priority = NotificationCompat.PRIORITY_DEFAULT
            it.setAutoCancel(true)

            tryNotify(it.build(), id)
        }
    }

    internal fun notifyError(message: String) {
        notifyMessage(message, NOTIFICATION_ERROR_ID, NOTIFICATION_ERROR_CHANNEL)
    }

    internal fun tryNotify(notification: Notification, id: Int) {
        if (ActivityCompat.checkSelfPermission(
                this,
                Manifest.permission.POST_NOTIFICATIONS
            ) == PackageManager.PERMISSION_GRANTED
        ) {
            notificationManager.notify(id, notification)
        }
    }

    internal fun cancelNotification(id: Int) {
        notificationManager.cancel(id)
    }

    internal fun close() {
        stopForeground(true)
        stopSelf()
    }

    override fun onDestroy() {
        logWriter?.write("Terminate VPN connection")
        logWriter?.close()
        logWriter = null

        controller?.kill(false, null)
        controller = null

        scope.cancel()

        setRootState(false)
        SmartConnect.onSessionClosed(prefs, SmartConnect.sessionToken(prefs))
        prefs.unregisterOnSharedPreferenceChangeListener(listener)
    }
}
