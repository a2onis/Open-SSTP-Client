package kittoku.osc.control

import kittoku.osc.preference.AUTH_PROTOCOL_EAP_MSCHAPv2
import kittoku.osc.preference.AUTH_PROTOCOL_MSCHAPv2
import kittoku.osc.preference.AUTH_PROTOCOl_PAP
import kotlinx.serialization.Serializable
import java.util.Locale

/**
 * Pure building blocks of the smart-connect engine.
 *
 * Nothing in this file touches Android APIs, so the decision logic (MTU sizing,
 * retry ladder, endpoint ranking) can be unit tested on the JVM.
 */

/** Outer IP/TCP headers, TLS records and SSTP/PPP framing removed from the path MTU. */
internal const val SSTP_TUNNEL_OVERHEAD = 60

internal const val SMART_MIN_MTU = 576
internal const val SMART_MAX_MTU = 1500

/** Used when the path MTU cannot be detected. 1400 fits LTE, most Wi-Fi and PPPoE links. */
internal const val SMART_FALLBACK_MTU = 1400

internal const val TLS_VERSION_DEFAULT = "DEFAULT"
internal const val TLS_VERSION_12 = "TLSv1.2"

internal val SMART_MTU_LADDER = intArrayOf(1400, 1280)

internal enum class TunnelTransport { WIFI, CELLULAR, ETHERNET, OTHER, UNKNOWN }

internal enum class SmartStep { AUTH, TLS, MTU, ENDPOINT }

/**
 * One concrete attempt of the retry ladder: which setting to change before the
 * next connection attempt. A `null` field means "keep the current value".
 */
internal data class SmartStepPlan(
    val step: SmartStep,
    val title: String,
    val authProtocols: Set<String>? = null,
    val tlsVersion: String? = null,
    val mtu: Int? = null,
)

/**
 * Derives the tunnel MTU from the underlying network's MTU.
 *
 * An SSTP data packet carries an IP packet inside PPP, SSTP, TLS, TCP and the
 * outer IP header. Advertising a 1500 byte MTU on a 1500 byte link therefore
 * produces fragmented or dropped packets; sizing the tunnel from the real path
 * MTU keeps TCP segments whole, which is the single biggest throughput and
 * latency win for a userspace SSTP client.
 */
internal fun recommendedMtu(linkMtu: Int): Int {
    if (linkMtu < 1000) return SMART_FALLBACK_MTU // undetectable, 0, or an exotic link
    return (linkMtu - SSTP_TUNNEL_OVERHEAD).coerceIn(SMART_MIN_MTU, SMART_MAX_MTU)
}

/** The user's manual MTU always wins when it is smaller than the auto-tuned value. */
internal fun effectiveMtu(configuredMtu: Int, tunedMtu: Int, autoTune: Boolean): Int {
    if (!autoTune) return configuredMtu
    val safeTuned = tunedMtu.coerceIn(SMART_MIN_MTU, SMART_MAX_MTU)
    return minOf(configuredMtu, safeTuned)
}

/** MRU never exceeds the MTU, otherwise the server may send packets larger than the tunnel. */
internal fun effectiveMru(configuredMru: Int, mtu: Int): Int = minOf(configuredMru, mtu)

/** Exponential backoff: 2 s, 4 s, 8 s, 16 s, then 30 s. */
internal fun retryDelayMs(failureCount: Int): Long {
    val shift = failureCount.coerceIn(0, 4)
    return (2_000L shl shift).coerceAtMost(30_000L)
}

/**
 * Authentication protocols to try, most likely first, without enabling any
 * protocol the user did not select.
 */
internal fun authAttempts(current: Set<String>): List<Set<String>> {
    val ladder = mutableListOf<Set<String>>()

    fun add(candidate: Set<String>) {
        if (candidate.isNotEmpty() && candidate.all { it in current } && ladder.none { it == candidate }) {
            ladder.add(candidate)
        }
    }

    add(setOf(AUTH_PROTOCOL_MSCHAPv2))
    add(setOf(AUTH_PROTOCOl_PAP))
    add(setOf(AUTH_PROTOCOL_EAP_MSCHAPv2))
    add(setOf(AUTH_PROTOCOl_PAP, AUTH_PROTOCOL_MSCHAPv2))

    return ladder
}

/**
 * Orders retry strategies by the layer that failed: a TLS failure retries with a
 * pinned TLS version first, an authentication failure retries with the other
 * protocol first. Every strategy is cheap, so all of them stay in the ladder.
 */
internal fun orderedSteps(lastFailureStage: String): List<SmartStep> = when {
    lastFailureStage.startsWith("PAP") || lastFailureStage.startsWith("CHAP") ||
        lastFailureStage.startsWith("EAP") || lastFailureStage.startsWith("MSCHAPV2") ->
        listOf(SmartStep.AUTH, SmartStep.TLS, SmartStep.MTU, SmartStep.ENDPOINT)

    lastFailureStage.startsWith("SSL") || lastFailureStage.startsWith("SSTP") ->
        listOf(SmartStep.TLS, SmartStep.MTU, SmartStep.AUTH, SmartStep.ENDPOINT)

    lastFailureStage.startsWith("LCP") || lastFailureStage.startsWith("IPCP") ||
        lastFailureStage.startsWith("IPV6CP") ->
        listOf(SmartStep.MTU, SmartStep.AUTH, SmartStep.TLS, SmartStep.ENDPOINT)

    else -> listOf(SmartStep.AUTH, SmartStep.TLS, SmartStep.MTU, SmartStep.ENDPOINT)
}

/**
 * Builds the deterministic ladder of settings changes tried before giving up or
 * switching to another saved configuration.
 */
internal fun planSteps(
    configuredAuth: Set<String>,
    tunedMtu: Int,
    lastFailureStage: String,
): List<SmartStepPlan> {
    val plans = mutableListOf<SmartStepPlan>()

    orderedSteps(lastFailureStage).forEach { step ->
        when (step) {
            SmartStep.AUTH -> {
                authAttempts(configuredAuth)
                    .filter { it != configuredAuth }
                    .forEach { candidate ->
                        plans.add(
                            SmartStepPlan(
                                step = SmartStep.AUTH,
                                title = "Alternate authentication (${candidate.sorted().joinToString("/")})",
                                authProtocols = candidate,
                            )
                        )
                    }
            }

            SmartStep.TLS -> plans.add(
                SmartStepPlan(
                    step = SmartStep.TLS,
                    title = "Pin $TLS_VERSION_12",
                    tlsVersion = TLS_VERSION_12,
                )
            )

            SmartStep.MTU -> SMART_MTU_LADDER.forEach { candidate ->
                val target = minOf(tunedMtu, candidate)
                if (target < tunedMtu && plans.none { it.step == SmartStep.MTU && it.mtu == target }) {
                    plans.add(SmartStepPlan(step = SmartStep.MTU, title = "Lower MTU to $target", mtu = target))
                }
            }

            SmartStep.ENDPOINT -> plans.add(
                SmartStepPlan(step = SmartStep.ENDPOINT, title = "Try the next best configuration")
            )
        }
    }

    return plans
}

internal fun describeTransport(transport: TunnelTransport): String = when (transport) {
    TunnelTransport.WIFI -> "Wi-Fi"
    TunnelTransport.CELLULAR -> "mobile data"
    TunnelTransport.ETHERNET -> "Ethernet"
    TunnelTransport.OTHER -> "network"
    TunnelTransport.UNKNOWN -> "unknown network"
}

/** Compact session timer, e.g. `12:04` or `1:02:59`. */
internal fun formatSessionDuration(millis: Long): String {
    val totalSeconds = (millis.coerceAtLeast(0L)) / 1000
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60

    return if (hours > 0) {
        String.format(Locale.US, "%d:%02d:%02d", hours, minutes, seconds)
    } else {
        String.format(Locale.US, "%02d:%02d", minutes, seconds)
    }
}

/** Per-endpoint history, persisted so a later session starts with what already worked. */
@Serializable
internal data class EndpointStat(
    val attempts: Int = 0,
    val successes: Int = 0,
    val failures: Int = 0,
    val lastHandshakeMs: Long = 0L,
    val bestHandshakeMs: Long = 0L,
    val lastConnectedAt: Long = 0L,
    val lastError: String = "",
    val lastAuth: String = "",
    val lastMtu: Int = 0,
    val lastTransport: String = "",
)

@Serializable
internal data class SmartStats(
    val endpoints: Map<String, EndpointStat> = emptyMap(),
)

internal fun EndpointStat.successRatio(): Double {
    if (attempts <= 0) return 0.0
    return (successes.toDouble() / attempts).coerceIn(0.0, 1.0)
}

/**
 * Quality score used to rank saved configurations. Successful connections with a
 * fast handshake and a recent win score highest; repeated failures push a server
 * down without ever hiding it from the list.
 */
internal fun EndpointStat.qualityScore(now: Long = System.currentTimeMillis()): Double {
    if (attempts <= 0) return 0.0

    val ratio = successRatio()
    val speed = if (bestHandshakeMs > 0) 1.0 / (1.0 + bestHandshakeMs / 1_000.0) else 0.0
    val ageDays = if (lastConnectedAt > 0) (now - lastConnectedAt).coerceAtLeast(0L) / 86_400_000.0 else 365.0
    val freshness = 1.0 / (1.0 + ageDays)
    val penalty = failures * 0.05

    return ratio * 3.0 + speed + freshness * 0.5 - penalty
}

internal fun rankEndpointKeys(
    stats: SmartStats,
    keys: Collection<String>,
    now: Long = System.currentTimeMillis(),
): List<String> = keys.sortedWith(
    compareByDescending<String> { stats.endpoints[it]?.qualityScore(now) ?: 0.0 }
        .thenBy { it }
)

internal fun SmartStats.bestKey(
    keys: Collection<String>,
    now: Long = System.currentTimeMillis(),
): String? = rankEndpointKeys(this, keys, now).firstOrNull()
