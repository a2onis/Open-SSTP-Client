package kittoku.osc.control

import android.content.Context
import android.content.SharedPreferences
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import kittoku.osc.preference.ACTIVE_PROFILE_KEY
import kittoku.osc.preference.OscPrefKey
import kittoku.osc.preference.PROFILE_KEY_HEADER
import kittoku.osc.preference.Profile
import kittoku.osc.preference.accessor.getBooleanPrefValue
import kittoku.osc.preference.accessor.getIntPrefValue
import kittoku.osc.preference.accessor.getSetPrefValue
import kittoku.osc.preference.accessor.getStringPrefValue
import kittoku.osc.preference.accessor.setSetPrefValue
import kittoku.osc.preference.accessor.setStringPrefValue
import kittoku.osc.preference.deserializeProfile
import kittoku.osc.preference.importProfile
import kotlinx.serialization.json.Json

/**
 * Smart connect engine: tunes the tunnel to the current network, remembers what
 * worked per endpoint and drives the automatic retry/failover ladder.
 *
 * All state lives in the default shared preferences under the `smart.` prefix so
 * it is never written into exported profiles and never removed by the
 * temporary-preference cleanup.
 */
internal enum class SmartState { IDLE, CONNECTING, CONNECTED, RETRYING, FAILED }

internal data class RetryPlan(
    val attempt: Int,
    val totalAttempts: Int,
    val delayMs: Long,
    val detail: String,
    val endpoint: String,
)

internal object SmartConnect {
    private const val KEY_STATE = "smart.state"
    private const val KEY_DETAIL = "smart.detail"
    private const val KEY_ATTEMPT = "smart.attempt"
    private const val KEY_STARTED_AT = "smart.started_at"
    private const val KEY_CONNECTED_AT = "smart.connected_at"
    private const val KEY_RETRY_AT = "smart.retry_at"
    private const val KEY_RETRY_DELAY = "smart.retry_delay"
    private const val KEY_MTU = "smart.mtu"
    private const val KEY_TUNED_MTU = "smart.tuned_mtu"
    private const val KEY_LINK_MTU = "smart.link_mtu"
    private const val KEY_TRANSPORT = "smart.transport"
    private const val KEY_TLS = "smart.tls"
    private const val KEY_CIPHER = "smart.cipher"
    private const val KEY_IP = "smart.ip"
    private const val KEY_DNS = "smart.dns"
    private const val KEY_ENDPOINT = "smart.endpoint"
    private const val KEY_LAST_STAGE = "smart.last_stage"
    private const val KEY_LAST_ERROR = "smart.last_error"
    private const val KEY_STATS = "smart.stats"
    private const val KEY_ORIGIN_AUTH = "smart.origin_auth"
    private const val KEY_ORIGIN_TLS = "smart.origin_tls"
    private const val KEY_SESSION = "smart.session"

    private const val MAX_STATS_ENTRIES = 100

    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

    // ------------------------------------------------------------------ flags

    internal fun isEnabled(prefs: SharedPreferences) =
        getBooleanPrefValue(OscPrefKey.SMART_CONNECT_ENABLED, prefs)

    internal fun isAutoMtuEnabled(prefs: SharedPreferences) =
        isEnabled(prefs) && getBooleanPrefValue(OscPrefKey.SMART_AUTO_MTU, prefs)

    internal fun isFallbackEnabled(prefs: SharedPreferences) =
        isEnabled(prefs) && getBooleanPrefValue(OscPrefKey.SMART_AUTO_FALLBACK, prefs)

    internal fun isAutoEndpointEnabled(prefs: SharedPreferences) =
        isEnabled(prefs) && getBooleanPrefValue(OscPrefKey.SMART_AUTO_ENDPOINT, prefs)

    internal fun isFastReconnectEnabled(prefs: SharedPreferences) =
        isEnabled(prefs) && getBooleanPrefValue(OscPrefKey.SMART_FAST_RECONNECT, prefs)

    // ------------------------------------------------------------------ state

    internal fun state(prefs: SharedPreferences): SmartState =
        prefs.getString(KEY_STATE, null)
            ?.let { name -> SmartState.values().firstOrNull { it.name == name } }
            ?: SmartState.IDLE

    internal fun detail(prefs: SharedPreferences): String =
        prefs.getString(KEY_DETAIL, "").orEmpty()

    internal fun lastError(prefs: SharedPreferences): String =
        prefs.getString(KEY_LAST_ERROR, "").orEmpty()

    internal fun connectedAt(prefs: SharedPreferences): Long = prefs.getLong(KEY_CONNECTED_AT, 0L)

    internal fun retryAt(prefs: SharedPreferences): Long = prefs.getLong(KEY_RETRY_AT, 0L)

    internal fun retryDelayMs(prefs: SharedPreferences): Long = prefs.getLong(KEY_RETRY_DELAY, 0L)

    internal fun attempt(prefs: SharedPreferences): Int = prefs.getInt(KEY_ATTEMPT, 0)

    internal fun tunedMtu(prefs: SharedPreferences): Int =
        prefs.getInt(KEY_TUNED_MTU, SMART_FALLBACK_MTU).takeIf { it in SMART_MIN_MTU..SMART_MAX_MTU }
            ?: SMART_FALLBACK_MTU

    /** The MTU the PPP/IP layer should use right now, including any auto-tuned or retry value. */
    internal fun effectiveTunnelMtu(prefs: SharedPreferences): Int = effectiveMtu(
        getIntPrefValue(OscPrefKey.PPP_MTU, prefs),
        activeMtu(prefs),
        isAutoMtuEnabled(prefs),
    )

    internal fun activeMtu(prefs: SharedPreferences): Int =
        prefs.getInt(KEY_MTU, tunedMtu(prefs)).takeIf { it in SMART_MIN_MTU..SMART_MAX_MTU }
            ?: tunedMtu(prefs)

    internal fun endpoint(prefs: SharedPreferences): String =
        prefs.getString(KEY_ENDPOINT, null)?.takeIf { it.isNotBlank() } ?: endpointName(prefs)

    internal fun tlsVersion(prefs: SharedPreferences): String = prefs.getString(KEY_TLS, "").orEmpty()

    internal fun cipherSuite(prefs: SharedPreferences): String = prefs.getString(KEY_CIPHER, "").orEmpty()

    internal fun assignedAddress(prefs: SharedPreferences): String = prefs.getString(KEY_IP, "").orEmpty()

    internal fun dnsServers(prefs: SharedPreferences): String = prefs.getString(KEY_DNS, "").orEmpty()

    /**
     * Identifies the current connection attempt so a controller that is shutting
     * down cannot overwrite the state of a newer attempt.
     */
    internal fun sessionToken(prefs: SharedPreferences): Long = prefs.getLong(KEY_SESSION, 0L)

    internal fun markConnectRequested(prefs: SharedPreferences) {
        prefs.edit()
            .putString(KEY_STATE, SmartState.CONNECTING.name)
            .putString(KEY_DETAIL, "Starting tunnel")
            .putLong(KEY_RETRY_AT, 0L)
            .apply()
    }

    /**
     * Called by the VPN service right before a controller is created: detects the
     * path MTU, replays the strategy that worked last time and resets the ladder.
     */
    internal fun reserveForConnect(context: Context, prefs: SharedPreferences) {
        val linkMtu = linkMtuOf(context)
        val transport = transportOf(context)
        val endpoint = endpointName(prefs)
        val tuned = if (isAutoMtuEnabled(prefs)) recommendedMtu(linkMtu) else getIntPrefValue(OscPrefKey.PPP_MTU, prefs)
        val learned = readStats(prefs).endpoints[endpoint]

        val editor = prefs.edit()
        editor.putLong(KEY_SESSION, prefs.getLong(KEY_SESSION, 0L) + 1L)
        editor.putStringSet(KEY_ORIGIN_AUTH, getSetPrefValue(OscPrefKey.PPP_AUTH_PROTOCOLS, prefs).toSet())
        editor.putString(KEY_ORIGIN_TLS, getStringPrefValue(OscPrefKey.SSL_VERSION, prefs))
        editor.putInt(KEY_ATTEMPT, 0)
        editor.putLong(KEY_STARTED_AT, System.currentTimeMillis())
        editor.putLong(KEY_CONNECTED_AT, 0L)
        editor.putLong(KEY_RETRY_AT, 0L)
        editor.putLong(KEY_RETRY_DELAY, 0L)
        editor.putInt(KEY_LINK_MTU, linkMtu)
        editor.putInt(KEY_TUNED_MTU, tuned)
        editor.putInt(KEY_MTU, tuned)
        editor.putString(KEY_TRANSPORT, transport.name)
        editor.putString(KEY_ENDPOINT, endpoint)
        editor.putString(KEY_LAST_STAGE, "")
        editor.putString(KEY_STATE, SmartState.CONNECTING.name)
        editor.putString(KEY_DETAIL, "Negotiating with $endpoint")
        editor.apply()

        if (!isEnabled(prefs)) return

        countAttempt(prefs, endpoint)

        if (learned == null || learned.successes <= 0) return

        // Replay what already worked on this endpoint instead of starting from scratch.
        val configuredAuth = getSetPrefValue(OscPrefKey.PPP_AUTH_PROTOCOLS, prefs)
        if (learned.lastAuth.isNotEmpty() && learned.lastAuth in configuredAuth) {
            setSetPrefValue(setOf(learned.lastAuth), OscPrefKey.PPP_AUTH_PROTOCOLS, prefs)
        }

        if (isAutoMtuEnabled(prefs) && learned.lastMtu in SMART_MIN_MTU..SMART_MAX_MTU) {
            prefs.edit().putInt(KEY_MTU, minOf(tuned, learned.lastMtu)).apply()
        }
    }

    /** Records the failing layer so the next attempt starts with the right strategy. */
    internal fun noteFailure(prefs: SharedPreferences, where: String, result: String) {
        val endpoint = prefs.getString(KEY_ENDPOINT, null) ?: endpointName(prefs)
        val message = "$where: $result"

        prefs.edit()
            .putString(KEY_LAST_STAGE, where)
            .putString(KEY_LAST_ERROR, message)
            .putString(KEY_STATE, SmartState.RETRYING.name)
            .apply()

        if (!isEnabled(prefs)) return

        val stats = readStats(prefs)
        val previous = stats.endpoints[endpoint] ?: EndpointStat()
        val updated = previous.copy(
            failures = previous.failures + 1,
            lastError = message,
        )
        writeStats(stats.copy(endpoints = stats.endpoints + (endpoint to updated)), prefs)
    }

    /** Called once the tunnel is up: remembers the winning strategy for this endpoint. */
    internal fun onConnected(
        prefs: SharedPreferences,
        mtu: Int,
        auth: String,
        tls: String,
        cipher: String,
    ) {
        val now = System.currentTimeMillis()
        val endpoint = prefs.getString(KEY_ENDPOINT, null) ?: endpointName(prefs)
        val started = prefs.getLong(KEY_STARTED_AT, 0L)
        val handshakeMs = if (started in 1 until now) now - started else 0L

        prefs.edit()
            .putString(KEY_STATE, SmartState.CONNECTED.name)
            .putString(KEY_DETAIL, "Connected to $endpoint")
            .putLong(KEY_CONNECTED_AT, now)
            .putLong(KEY_RETRY_AT, 0L)
            .putLong(KEY_RETRY_DELAY, 0L)
            .putString(KEY_TLS, tls)
            .putString(KEY_CIPHER, cipher)
            .putInt(KEY_MTU, mtu)
            .putString(KEY_LAST_ERROR, "")
            .apply()

        if (!isEnabled(prefs)) return

        val stats = readStats(prefs)
        val previous = stats.endpoints[endpoint] ?: EndpointStat()
        val bestHandshake = if (handshakeMs <= 0) previous.bestHandshakeMs
        else if (previous.bestHandshakeMs <= 0 || handshakeMs < previous.bestHandshakeMs) handshakeMs
        else previous.bestHandshakeMs

        val updated = previous.copy(
            successes = previous.successes + 1,
            lastHandshakeMs = handshakeMs,
            bestHandshakeMs = bestHandshake,
            lastConnectedAt = now,
            lastError = "",
            lastAuth = auth,
            lastMtu = mtu,
            lastTransport = prefs.getString(KEY_TRANSPORT, "").orEmpty(),
        )
        writeStats(stats.copy(endpoints = stats.endpoints + (endpoint to updated)), prefs)
    }

    /**
     * Consumes the next ladder step. Returns `null` when every strategy has been
     * tried, in which case the original settings are restored and the session is
     * reported as failed.
     */
    internal fun nextPlan(context: Context, prefs: SharedPreferences): RetryPlan? {
        val configuredAuth = prefs.getStringSet(KEY_ORIGIN_AUTH, null)?.toSet()
            ?: getSetPrefValue(OscPrefKey.PPP_AUTH_PROTOCOLS, prefs)
        val tuned = tunedMtu(prefs)
        val stage = prefs.getString(KEY_LAST_STAGE, "").orEmpty()
        val plans = planSteps(configuredAuth, tuned, stage)
        val index = prefs.getInt(KEY_ATTEMPT, 0)

        if (index >= plans.size) {
            giveUp(prefs)
            return null
        }

        val plan = plans[index]
        val description = applyPlan(context, prefs, plan)
        if (description == null) {
            giveUp(prefs)
            return null
        }

        val attempt = index + 1
        val delay = if (isFastReconnectEnabled(prefs)) {
            retryDelayMs(index)
        } else {
            getIntPrefValue(OscPrefKey.RECONNECTION_INTERVAL, prefs) * 1000L
        }
        val endpoint = prefs.getString(KEY_ENDPOINT, null) ?: endpointName(prefs)
        if (isEnabled(prefs)) {
            countAttempt(prefs, endpoint)
        }

        prefs.edit()
            .putInt(KEY_ATTEMPT, attempt)
            .putLong(KEY_RETRY_AT, System.currentTimeMillis() + delay)
            .putLong(KEY_RETRY_DELAY, delay)
            .putString(KEY_STATE, SmartState.RETRYING.name)
            .putString(KEY_DETAIL, "$description · retry ${attempt}/${plans.size}")
            .apply()

        return RetryPlan(attempt, plans.size, delay, description, endpoint)
    }

    /** Restores the user's own settings and reports the final failure. */
    internal fun giveUp(prefs: SharedPreferences, token: Long? = null): String {
        if (token != null && token != sessionToken(prefs)) {
            return "Stale attempt ignored"
        }

        restoreOriginals(prefs)

        val endpoint = prefs.getString(KEY_ENDPOINT, null) ?: endpointName(prefs)
        val message = "Could not connect to $endpoint"

        prefs.edit()
            .putString(KEY_STATE, SmartState.FAILED.name)
            .putString(KEY_DETAIL, message)
            .putLong(KEY_RETRY_AT, 0L)
            .putLong(KEY_RETRY_DELAY, 0L)
            .apply()

        return message
    }

    /** Keeps a failure visible in the UI after the service shuts down. */
    internal fun onSessionClosed(prefs: SharedPreferences, token: Long? = null) {
        if (state(prefs) == SmartState.FAILED) return
        onDisconnected(prefs, token)
    }

    internal fun onDisconnected(prefs: SharedPreferences, token: Long? = null) {
        if (token != null && token != sessionToken(prefs)) return
        // A terminal failure stays visible until the next connect request.
        if (state(prefs) == SmartState.FAILED) return

        prefs.edit()
            .putString(KEY_STATE, SmartState.IDLE.name)
            .putString(KEY_DETAIL, "Disconnected")
            .putLong(KEY_CONNECTED_AT, 0L)
            .putLong(KEY_RETRY_AT, 0L)
            .putLong(KEY_RETRY_DELAY, 0L)
            .putInt(KEY_ATTEMPT, 0)
            .putString(KEY_TLS, "")
            .putString(KEY_CIPHER, "")
            .putString(KEY_IP, "")
            .putString(KEY_DNS, "")
            .apply()
    }

    /** Network information reported by the VPN link, used by the home card. */
    internal fun noteSession(
        prefs: SharedPreferences,
        address: String,
        dns: String,
        tls: String?,
        cipher: String?,
    ) {
        val editor = prefs.edit().putString(KEY_IP, address).putString(KEY_DNS, dns)
        if (!tls.isNullOrBlank()) editor.putString(KEY_TLS, tls)
        if (!cipher.isNullOrBlank()) editor.putString(KEY_CIPHER, cipher)
        editor.apply()
    }

    // -------------------------------------------------------------- settings

    internal fun endpointName(prefs: SharedPreferences): String {
        val profile = prefs.getString(ACTIVE_PROFILE_KEY, null)
        if (!profile.isNullOrBlank()) return profile

        val host = getStringPrefValue(OscPrefKey.HOME_HOSTNAME, prefs)
        val port = getIntPrefValue(OscPrefKey.SSL_PORT, prefs)
        return if (host.isBlank()) "unconfigured" else "$host:$port"
    }

    internal fun transportOf(context: Context): TunnelTransport {
        val manager = context.getSystemService(ConnectivityManager::class.java)
            ?: return TunnelTransport.UNKNOWN
        val capabilities = manager.getNetworkCapabilities(manager.activeNetwork)
            ?: return TunnelTransport.UNKNOWN

        return when {
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> TunnelTransport.WIFI
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> TunnelTransport.CELLULAR
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> TunnelTransport.ETHERNET
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN) -> TunnelTransport.OTHER
            else -> TunnelTransport.OTHER
        }
    }

    internal fun linkMtuOf(context: Context): Int {
        val manager = context.getSystemService(ConnectivityManager::class.java) ?: return 0
        return manager.getLinkProperties(manager.activeNetwork)?.mtu ?: 0
    }

    internal fun tuningSummary(context: Context, prefs: SharedPreferences): String {
        if (!isEnabled(prefs)) {
            return "Smart connect is off · using manual MRU ${getIntPrefValue(OscPrefKey.PPP_MRU, prefs)} / MTU ${getIntPrefValue(OscPrefKey.PPP_MTU, prefs)}"
        }

        val link = linkMtuOf(context)
        val tuned = if (isAutoMtuEnabled(prefs)) recommendedMtu(link) else tunedMtu(prefs)
        val mtu = effectiveMtu(getIntPrefValue(OscPrefKey.PPP_MTU, prefs), activeMtu(prefs), isAutoMtuEnabled(prefs))
        val transport = describeTransport(transportOf(context))
        val linkText = if (link > 0) "path $link" else "path unknown"
        val history = readStats(prefs).endpoints[endpointName(prefs)]?.let { stat ->
            val rate = "${stat.successes}/${stat.attempts} successful"
            if (stat.bestHandshakeMs > 0) "$rate · best ${stat.bestHandshakeMs} ms" else rate
        } ?: "no history yet"

        val mtuNote = if (isAutoMtuEnabled(prefs)) "" else " · auto MTU off (tuned $tuned)"
        return "MTU $mtu ($linkText, $transport)$mtuNote · $history"
    }

    internal fun sessionSummary(prefs: SharedPreferences): String {
        val parts = mutableListOf<String>()
        val tls = tlsVersion(prefs)
        val cipher = cipherSuite(prefs)
        if (tls.isNotEmpty()) parts.add(tls)
        if (cipher.isNotEmpty()) parts.add(cipher)
        if (state(prefs) == SmartState.CONNECTED) parts.add("MTU ${activeMtu(prefs)}")
        return parts.joinToString(" · ")
    }

    internal fun statsSummary(prefs: SharedPreferences): String {
        val stats = readStats(prefs)
        if (stats.endpoints.isEmpty()) return "No connection history yet. Connect once to start learning."

        val now = System.currentTimeMillis()
        return stats.endpoints.entries
            .sortedByDescending { it.value.qualityScore(now) }
            .joinToString("\n\n") { (name, stat) ->
                buildString {
                    append(name)
                    append("\n")
                    append("${stat.successes}/${stat.attempts} successful")
                    if (stat.bestHandshakeMs > 0) append(" · best handshake ${stat.bestHandshakeMs} ms")
                    if (stat.lastMtu > 0) append(" · MTU ${stat.lastMtu}")
                    if (stat.lastAuth.isNotEmpty()) append(" · ${stat.lastAuth}")
                    if (stat.lastError.isNotEmpty()) append("\nLast error: ${stat.lastError}")
                }
            }
    }

    internal fun savedProfiles(prefs: SharedPreferences): List<Pair<String, Profile>> =
        prefs.all.keys
            .filter { it.startsWith(PROFILE_KEY_HEADER) }
            .mapNotNull { key ->
                val profile = prefs.getString(key, null)?.let { deserializeProfile(it) }
                if (profile == null) null else key to profile
            }

    /** The saved configuration with the best history, preferring ones with credentials. */
    internal fun bestSavedProfile(prefs: SharedPreferences): Pair<String, Profile>? {
        val usable = savedProfiles(prefs).filter {
            it.second.stringSetting[OscPrefKey.HOME_HOSTNAME.name].orEmpty().isNotBlank()
        }
        if (usable.isEmpty()) return null

        val withCredentials = usable.filter {
            it.second.stringSetting[OscPrefKey.HOME_USERNAME.name].orEmpty().isNotBlank()
        }
        val pool = withCredentials.ifEmpty { usable }
        val stats = readStats(prefs)
        val names = pool.map { it.first.removePrefix(PROFILE_KEY_HEADER) }
        val best = stats.bestKey(names) ?: return pool.first()

        return pool.firstOrNull { it.first.removePrefix(PROFILE_KEY_HEADER) == best } ?: pool.first()
    }

    internal fun resetLearned(prefs: SharedPreferences) {
        prefs.edit()
            .remove(KEY_STATS)
            .remove(KEY_LAST_STAGE)
            .remove(KEY_LAST_ERROR)
            .putInt(KEY_ATTEMPT, 0)
            .apply()
    }

    // ------------------------------------------------------------ statistics

    internal fun readStats(prefs: SharedPreferences): SmartStats {
        val serialized = prefs.getString(KEY_STATS, null) ?: return SmartStats()
        return runCatching { json.decodeFromString(SmartStats.serializer(), serialized) }
            .getOrElse { SmartStats() }
    }

    private fun writeStats(stats: SmartStats, prefs: SharedPreferences) {
        val trimmed = if (stats.endpoints.size <= MAX_STATS_ENTRIES) stats else SmartStats(
            stats.endpoints.entries
                .sortedByDescending { it.value.lastConnectedAt }
                .take(MAX_STATS_ENTRIES)
                .associate { it.key to it.value }
        )
        prefs.edit()
            .putString(KEY_STATS, json.encodeToString(SmartStats.serializer(), trimmed))
            .apply()
    }

    // --------------------------------------------------------------- internal

    /** Applies one ladder step and returns a human readable description. */
    private fun applyPlan(context: Context, prefs: SharedPreferences, plan: SmartStepPlan): String? {
        // Every step starts from the user's own strategy so the steps stay independent.
        restoreOriginals(prefs)

        return when (plan.step) {
            SmartStep.AUTH -> {
                val auth = plan.authProtocols ?: return null
                setSetPrefValue(auth, OscPrefKey.PPP_AUTH_PROTOCOLS, prefs)
                "${plan.title}"
            }

            SmartStep.TLS -> {
                val tls = plan.tlsVersion ?: return null
                setStringPrefValue(tls, OscPrefKey.SSL_VERSION, prefs)
                plan.title
            }

            SmartStep.MTU -> {
                val mtu = plan.mtu ?: return null
                prefs.edit().putInt(KEY_MTU, minOf(tunedMtu(prefs), mtu)).apply()
                plan.title
            }

            SmartStep.ENDPOINT -> switchEndpoint(context, prefs)
        }
    }

    /** Switches to the next best saved configuration; every other knob is re-tuned for it. */
    private fun switchEndpoint(context: Context, prefs: SharedPreferences): String? {
        if (!isAutoEndpointEnabled(prefs)) return null

        val current = endpointName(prefs)
        val candidates = savedProfiles(prefs).filter { (key, profile) ->
            val name = key.removePrefix(PROFILE_KEY_HEADER)
            val host = profile.stringSetting[OscPrefKey.HOME_HOSTNAME.name].orEmpty()
            val username = profile.stringSetting[OscPrefKey.HOME_USERNAME.name].orEmpty()
            name != current && host.isNotBlank() && username.isNotBlank()
        }
        if (candidates.isEmpty()) return null

        val stats = readStats(prefs)
        val names = candidates.map { it.first.removePrefix(PROFILE_KEY_HEADER) }
        val chosenName = stats.bestKey(names) ?: return null
        val chosen = candidates
            .firstOrNull { it.first.removePrefix(PROFILE_KEY_HEADER) == chosenName }
            ?.second ?: return null

        importProfile(chosen, prefs)
        val linkMtu = linkMtuOf(context)
        val tuned = if (isAutoMtuEnabled(prefs)) recommendedMtu(linkMtu) else getIntPrefValue(OscPrefKey.PPP_MTU, prefs)

        prefs.edit()
            .putString(ACTIVE_PROFILE_KEY, chosenName)
            .putString(KEY_ENDPOINT, chosenName)
            .putInt(KEY_LINK_MTU, linkMtu)
            .putInt(KEY_TUNED_MTU, tuned)
            .putInt(KEY_MTU, tuned)
            .putString(KEY_TRANSPORT, transportOf(context).name)
            .putStringSet(KEY_ORIGIN_AUTH, getSetPrefValue(OscPrefKey.PPP_AUTH_PROTOCOLS, prefs).toSet())
            .putString(KEY_ORIGIN_TLS, getStringPrefValue(OscPrefKey.SSL_VERSION, prefs))
            .putString(KEY_LAST_STAGE, "")
            .apply()

        return "Switched to $chosenName"
    }

    /** Counts one connection attempt so the success ratio stays meaningful. */
    private fun countAttempt(prefs: SharedPreferences, endpoint: String) {
        val stats = readStats(prefs)
        val previous = stats.endpoints[endpoint] ?: EndpointStat()
        val updated = previous.copy(attempts = previous.attempts + 1)
        writeStats(stats.copy(endpoints = stats.endpoints + (endpoint to updated)), prefs)
    }

    private fun restoreOriginals(prefs: SharedPreferences) {
        prefs.getStringSet(KEY_ORIGIN_AUTH, null)?.takeIf { it.isNotEmpty() }?.also {
            setSetPrefValue(it.toSet(), OscPrefKey.PPP_AUTH_PROTOCOLS, prefs)
        }
        prefs.getString(KEY_ORIGIN_TLS, null)?.takeIf { it.isNotEmpty() }?.also {
            setStringPrefValue(it, OscPrefKey.SSL_VERSION, prefs)
        }
        prefs.edit().putInt(KEY_MTU, tunedMtu(prefs)).apply()
    }
}
