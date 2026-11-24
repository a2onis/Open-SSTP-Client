package kittoku.osc.control

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class SmartTuningTest {
    @Test
    fun mtuFollowsThePathMtu() {
        assertEquals(1440, recommendedMtu(1500))
        assertEquals(1360, recommendedMtu(1420))
        assertEquals(1220, recommendedMtu(1280))
        assertEquals(1500, recommendedMtu(9000))
    }

    @Test
    fun undetectablePathMtuUsesTheSafeDefault() {
        assertEquals(1400, recommendedMtu(0))
        assertEquals(1400, recommendedMtu(-1))
        assertEquals(1400, recommendedMtu(900))
    }

    @Test
    fun autoTuningNeverRaisesAManualMtu() {
        assertEquals(1440, effectiveMtu(1500, 1440, true))
        assertEquals(1200, effectiveMtu(1200, 1440, true))
        assertEquals(1500, effectiveMtu(1500, 1440, false))
        // A tuned value above the configured MTU is clamped by the user setting.
        assertEquals(1500, effectiveMtu(1500, 4000, true))
    }

    @Test
    fun mruNeverExceedsMtu() {
        assertEquals(1440, effectiveMru(1500, 1440))
        assertEquals(1200, effectiveMru(1200, 1440))
    }

    @Test
    fun backoffDoublesAndCaps() {
        assertEquals(2000L, retryDelayMs(0))
        assertEquals(4000L, retryDelayMs(1))
        assertEquals(8000L, retryDelayMs(2))
        assertEquals(16000L, retryDelayMs(3))
        assertEquals(30000L, retryDelayMs(4))
        assertEquals(30000L, retryDelayMs(9))
    }

    @Test
    fun authLadderStaysWithinTheSelectedProtocols() {
        val enabled = setOf("PAP", "MSCHAPv2")
        val ladder = authAttempts(enabled)

        assertTrue(ladder.isNotEmpty())
        assertTrue(ladder.all { candidate -> candidate.all { it in enabled } })
        assertEquals(setOf("MSCHAPv2"), ladder.first())
        assertEquals(listOf(setOf("PAP")), authAttempts(setOf("PAP")))
        assertTrue(authAttempts(emptySet()).isEmpty())
    }

    @Test
    fun failureStagePrioritisesTheMatchingStrategy() {
        assertEquals(SmartStep.AUTH, orderedSteps("CHAP:ERR_AUTHENTICATION_FAILED").first())
        assertEquals(SmartStep.TLS, orderedSteps("SSL:ERR_TIMEOUT").first())
        assertEquals(SmartStep.MTU, orderedSteps("LCP:ERR_TIMEOUT").first())
        assertEquals(SmartStep.AUTH, orderedSteps("").first())
    }

    @Test
    fun planKeepsStepsIndependent() {
        val plans = planSteps(setOf("PAP", "MSCHAPv2"), 1440, "SSL:ERR_TIMEOUT")

        assertEquals(SmartStep.TLS, plans.first().step)
        assertTrue(plans.any { it.step == SmartStep.MTU && it.mtu == 1400 })
        assertTrue(plans.any { it.step == SmartStep.MTU && it.mtu == 1280 })
        assertEquals(SmartStep.ENDPOINT, plans.last().step)
        assertTrue(
            plans.filter { it.step == SmartStep.AUTH }
                .all { plan -> plan.authProtocols!!.all { it == "PAP" || it == "MSCHAPv2" } }
        )
    }

    @Test
    fun planNeverRaisesTheMtu() {
        assertTrue(planSteps(setOf("PAP"), 1200, "LCP:ERR_TIMEOUT").none { it.step == SmartStep.MTU })
    }

    @Test
    fun rankingPrefersReliableAndFastEndpoints() {
        val now = 1_000_000_000_000L
        val stats = SmartStats(
            mapOf(
                "fast" to EndpointStat(attempts = 4, successes = 4, bestHandshakeMs = 400, lastConnectedAt = now),
                "slow" to EndpointStat(attempts = 4, successes = 4, bestHandshakeMs = 3_000, lastConnectedAt = now),
                "flaky" to EndpointStat(
                    attempts = 4,
                    successes = 1,
                    failures = 3,
                    bestHandshakeMs = 300,
                    lastConnectedAt = now,
                ),
            )
        )

        assertEquals(
            listOf("fast", "slow", "flaky"),
            rankEndpointKeys(stats, listOf("flaky", "slow", "fast"), now),
        )
        assertEquals("fast", stats.bestKey(listOf("flaky", "slow", "fast"), now))
    }

    @Test
    fun sessionDurationIsCompact() {
        assertEquals("00:05", formatSessionDuration(5_000))
        assertEquals("12:04", formatSessionDuration(724_000))
        assertEquals("1:02:59", formatSessionDuration(3_779_000))
        assertEquals("00:00", formatSessionDuration(-5))
    }
}
