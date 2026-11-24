package kittoku.osc.fragment

import android.app.Activity
import android.content.Intent
import android.content.SharedPreferences
import android.content.res.ColorStateList
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.HapticFeedbackConstants
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts.StartActivityForResult
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.isVisible
import androidx.preference.Preference
import androidx.preference.PreferenceFragmentCompat
import androidx.preference.PreferenceGroup
import androidx.preference.PreferenceManager
import androidx.preference.forEach
import kittoku.osc.R
import kittoku.osc.activity.MainActivity
import kittoku.osc.control.SmartConnect
import kittoku.osc.control.SmartState
import kittoku.osc.control.formatSessionDuration
import kittoku.osc.databinding.FragmentHomeBinding
import kittoku.osc.preference.ACTIVE_PROFILE_KEY
import kittoku.osc.preference.OscPrefKey
import kittoku.osc.preference.PROFILE_KEY_HEADER
import kittoku.osc.preference.accessor.getBooleanPrefValue
import kittoku.osc.preference.accessor.getIntPrefValue
import kittoku.osc.preference.accessor.getStringPrefValue
import kittoku.osc.preference.checkPreferences
import kittoku.osc.preference.custom.OscPreference
import kittoku.osc.preference.importProfile
import kittoku.osc.preference.toastInvalidSetting
import kittoku.osc.service.ACTION_VPN_CONNECT
import kittoku.osc.service.ACTION_VPN_DISCONNECT
import kittoku.osc.service.SstpVpnService


class HomeFragment : PreferenceFragmentCompat() {
    private lateinit var prefs: SharedPreferences
    private var binding: FragmentHomeBinding? = null

    private val handler = Handler(Looper.getMainLooper())

    private val ticker = object : Runnable {
        override fun run() {
            if (!isAdded) return

            updateCard()
            handler.postDelayed(this, 1_000L)
        }
    }

    // Listener callbacks run on the thread that wrote the preference, which is often
    // the VPN service, so every UI touch is posted to the main thread first.
    private val preferenceListener = SharedPreferences.OnSharedPreferenceChangeListener { _, key ->
        val root = view ?: return@OnSharedPreferenceChangeListener

        root.post {
            if (!isAdded) return@post

            updateCard()

            if (key == null || key == ACTIVE_PROFILE_KEY ||
                key == OscPrefKey.HOME_HOSTNAME.name || key == OscPrefKey.HOME_USERNAME.name ||
                key == OscPrefKey.HOME_PASSWORD.name || key == OscPrefKey.SSL_PORT.name
            ) {
                updatePreferenceSummaries()
            }
        }
    }

    private val preparationLauncher = registerForActivityResult(StartActivityForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) {
            startVpnService(ACTION_VPN_CONNECT)
        } else {
            SmartConnect.onDisconnected(prefs)
            updateCard()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = PreferenceManager.getDefaultSharedPreferences(requireContext())
    }

    override fun onCreatePreferences(savedInstanceState: Bundle?, rootKey: String?) {
        setPreferencesFromResource(R.xml.home, rootKey)

        findPreference<Preference>("manage_configurations")?.setOnPreferenceClickListener {
            (requireActivity() as MainActivity).openConfigurations()
            true
        }

        findPreference<Preference>("smart_overview")?.setOnPreferenceClickListener {
            (requireActivity() as MainActivity).openSettings()
            true
        }
    }

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        val preferenceView = super.onCreateView(inflater, container, savedInstanceState)!!
        val inflated = FragmentHomeBinding.inflate(inflater, container, false)

        inflated.preferenceContainer.addView(
            preferenceView,
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.MATCH_PARENT,
        )

        binding = inflated
        return inflated.root
    }

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)

        val inflated = binding ?: return

        inflated.connectButton.setOnClickListener {
            it.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)

            when {
                isActive() || SmartConnect.state(prefs) == SmartState.RETRYING -> requestDisconnect()
                else -> requestConnect()
            }
        }

        inflated.bestButton.setOnClickListener {
            it.performHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
            connectToBestServer()
        }
    }

    override fun onResume() {
        super.onResume()

        prefs.registerOnSharedPreferenceChangeListener(preferenceListener)
        updatePreferenceSummaries()
        updateCard()

        handler.removeCallbacks(ticker)
        handler.post(ticker)
    }

    override fun onPause() {
        handler.removeCallbacks(ticker)
        prefs.unregisterOnSharedPreferenceChangeListener(preferenceListener)

        super.onPause()
    }

    override fun onDestroyView() {
        handler.removeCallbacks(ticker)
        binding = null

        super.onDestroyView()
    }

    /** Used after a configuration was applied from the configurations screen. */
    internal fun connectNow() {
        if (!isActive() && SmartConnect.state(prefs) != SmartState.RETRYING) {
            requestConnect()
        }
    }

    private fun isActive(): Boolean =
        getBooleanPrefValue(OscPrefKey.HOME_CONNECTOR, prefs) ||
            getBooleanPrefValue(OscPrefKey.ROOT_STATE, prefs)

    private fun requestConnect() {
        checkPreferences(prefs)?.also { message ->
            toastInvalidSetting(message, requireContext())
            return
        }

        SmartConnect.markConnectRequested(prefs)
        updateCard()

        VpnService.prepare(requireContext())?.also { intent ->
            preparationLauncher.launch(intent)
        } ?: startVpnService(ACTION_VPN_CONNECT)
    }

    private fun requestDisconnect() {
        startVpnService(ACTION_VPN_DISCONNECT)
    }

    /**
     * One tap to the most reliable saved server: applies the configuration with the
     * best history and connects immediately.
     */
    private fun connectToBestServer() {
        if (isActive()) {
            Toast.makeText(requireContext(), R.string.toast_disconnect_first, Toast.LENGTH_SHORT).show()
            return
        }

        val best = SmartConnect.bestSavedProfile(prefs)
        if (best == null) {
            Toast.makeText(requireContext(), R.string.toast_no_saved_config, Toast.LENGTH_LONG).show()
            (requireActivity() as MainActivity).openConfigurations()
            return
        }

        val name = best.first.removePrefix(PROFILE_KEY_HEADER)
        importProfile(best.second, prefs)
        prefs.edit().putString(ACTIVE_PROFILE_KEY, name).apply()

        Toast.makeText(requireContext(), getString(R.string.toast_using_best, name), Toast.LENGTH_SHORT).show()
        updatePreferenceSummaries()
        requestConnect()
    }

    private fun startVpnService(action: String) {
        val intent = Intent(requireContext(), SstpVpnService::class.java).setAction(action)

        if (action == ACTION_VPN_CONNECT && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            requireContext().startForegroundService(intent)
        } else {
            requireContext().startService(intent)
        }
    }

    private fun updatePreferenceSummaries() {
        if (!isAdded) return

        val profile = prefs.getString(ACTIVE_PROFILE_KEY, null)

        findPreference<Preference>("manage_configurations")?.summary = when {
            profile.isNullOrBlank() -> "Select, manage, and find configurations"
            else -> "Last applied: $profile · tap to manage"
        }

        findPreference<Preference>("smart_overview")?.summary =
            SmartConnect.tuningSummary(requireContext(), prefs)

        // A configuration may have been applied (or swapped by failover), so let
        // every preference row re-read the stored values.
        val groups = mutableListOf<PreferenceGroup>(preferenceScreen)
        while (groups.isNotEmpty()) {
            groups.removeAt(0).forEach { preference ->
                if (preference is OscPreference) {
                    preference.updateView()
                }

                if (preference is PreferenceGroup) {
                    groups.add(preference)
                }
            }
        }
    }

    private fun updateCard() {
        val inflated = binding ?: return
        if (!isAdded) return

        val state = SmartConnect.state(prefs)
        val active = isActive()
        val hostname = getStringPrefValue(OscPrefKey.HOME_HOSTNAME, prefs)
        val profile = prefs.getString(ACTIVE_PROFILE_KEY, null)

        val endpoint = when {
            !profile.isNullOrBlank() -> profile
            hostname.isNotBlank() -> "$hostname:${getIntPrefValue(OscPrefKey.SSL_PORT, prefs)}"
            else -> getString(R.string.label_no_configuration)
        }

        inflated.endpointText.text = endpoint
        inflated.autoBadge.text = getString(
            if (SmartConnect.isEnabled(prefs)) R.string.smart_badge else R.string.smart_badge_off
        )

        inflated.statusText.text = when {
            state == SmartState.RETRYING -> getString(R.string.status_retrying)
            active && state == SmartState.CONNECTED -> getString(R.string.status_connected)
            active -> getString(R.string.status_connecting)
            state == SmartState.FAILED -> getString(R.string.status_failed)
            else -> getString(R.string.status_disconnected)
        }

        val dotColor = when {
            active && state == SmartState.CONNECTED -> R.color.status_connected
            state == SmartState.RETRYING -> R.color.status_retrying
            state == SmartState.FAILED -> R.color.status_failed
            active -> R.color.status_connecting
            else -> R.color.status_idle
        }
        ViewCompat.setBackgroundTintList(
            inflated.statusDot,
            ColorStateList.valueOf(ContextCompat.getColor(requireContext(), dotColor)),
        )

        inflated.detailText.text = when {
            hostname.isBlank() -> getString(R.string.hero_hint_no_host)
            state == SmartState.RETRYING -> {
                val base = SmartConnect.detail(prefs).ifBlank { getString(R.string.status_retrying) }
                val retryAt = SmartConnect.retryAt(prefs)
                val seconds = if (retryAt > 0) {
                    ((retryAt - System.currentTimeMillis()) / 1_000L + 1).coerceAtLeast(0)
                } else {
                    0
                }

                if (seconds > 0) "$base · retry in ${seconds}s" else base
            }
            active && state == SmartState.CONNECTED ->
                SmartConnect.sessionSummary(prefs).ifBlank { getString(R.string.status_connected) }
            active -> "Negotiating with $endpoint"
            state == SmartState.FAILED ->
                SmartConnect.lastError(prefs).ifBlank { getString(R.string.status_failed) }
            else -> getString(R.string.hero_hint_idle)
        }

        val connectedAt = SmartConnect.connectedAt(prefs)
        if (active && connectedAt > 0) {
            val elapsed = formatSessionDuration(System.currentTimeMillis() - connectedAt)
            val details = mutableListOf(elapsed, "MTU ${SmartConnect.activeMtu(prefs)}")
            val address = SmartConnect.assignedAddress(prefs)
            if (address.isNotBlank()) details.add(address)

            inflated.sessionText.text = details.joinToString(" · ")
            inflated.sessionText.isVisible = true
        } else {
            inflated.sessionText.isVisible = false
        }

        inflated.connectButton.text = when {
            active -> getString(R.string.action_disconnect)
            state == SmartState.RETRYING -> getString(R.string.action_cancel)
            else -> getString(R.string.action_connect)
        }
        inflated.connectButton.setIconResource(
            if (active || state == SmartState.RETRYING) R.drawable.ic_baseline_close_24 else R.drawable.ic_power_24
        )
        inflated.bestButton.isEnabled = !active
    }
}
