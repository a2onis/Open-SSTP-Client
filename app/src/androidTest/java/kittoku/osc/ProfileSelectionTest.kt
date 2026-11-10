package kittoku.osc

import androidx.preference.PreferenceManager
import androidx.test.core.app.ActivityScenario
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.action.ViewActions.click
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kittoku.osc.activity.MainActivity
import kittoku.osc.preference.OscPrefKey
import kittoku.osc.preference.PROFILE_KEY_HEADER
import kittoku.osc.preference.Profile
import kittoku.osc.preference.accessor.getIntPrefValue
import kotlinx.serialization.json.Json
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProfileSelectionTest {
    private val prefs by lazy { PreferenceManager.getDefaultSharedPreferences(InstrumentationRegistry.getInstrumentation().targetContext) }

    @Before fun setup() {
        prefs.edit().clear().commit()
        val profile = Profile().apply {
            stringSetting[OscPrefKey.HOME_HOSTNAME.name] = "vpn.example.org"
            stringSetting[OscPrefKey.HOME_USERNAME.name] = "tester"
            intSetting[OscPrefKey.SSL_PORT.name] = 8443
        }
        prefs.edit().putString(PROFILE_KEY_HEADER + "Test VPN", Json.encodeToString(Profile.serializer(), profile)).commit()
    }

    @After fun cleanup() { prefs.edit().clear().commit() }

    @Test fun savedProfileActionsAreVisibleAndApplyToHomeSettings() {
        ActivityScenario.launch(MainActivity::class.java).use {
            onView(withText("Configurations")).perform(click())
            onView(withText("Test VPN")).perform(click())
            onView(withText("Apply as primary")).check(matches(isDisplayed())).perform(click())
            onView(withText("vpn.example.org")).check(matches(isDisplayed()))
            assertEquals("vpn.example.org", prefs.getString(OscPrefKey.HOME_HOSTNAME.name, null))
            assertEquals("tester", prefs.getString(OscPrefKey.HOME_USERNAME.name, null))
            assertEquals(8443, getIntPrefValue(OscPrefKey.SSL_PORT, prefs))
            assertEquals("Test VPN", prefs.getString("active_profile_name", null))
        }
    }
}
