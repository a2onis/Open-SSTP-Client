package kittoku.osc.preference

import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test

class ProfileTest {
    @Test fun savedProfileRetainsAdvancedSettings() {
        val original = Profile().apply {
            stringSetting[OscPrefKey.HOME_HOSTNAME.name] = "vpn.example.org"
            intSetting[OscPrefKey.SSL_PORT.name] = 443
            booleanSetting[OscPrefKey.SSL_DO_VERIFY.name] = true
            setSetting[OscPrefKey.PPP_AUTH_PROTOCOLS.name] = setOf("MSCHAPv2")
        }
        val restored = deserializeProfile(Json.encodeToString(Profile.serializer(), original))!!
        assertEquals(original.stringSetting, restored.stringSetting)
        assertEquals(original.intSetting, restored.intSetting)
        assertEquals(original.booleanSetting, restored.booleanSetting)
        assertEquals(original.setSetting, restored.setSetting)
    }

    @Test fun invalidProfileIsRejected() {
        assertNull(deserializeProfile("not json"))
    }
}
