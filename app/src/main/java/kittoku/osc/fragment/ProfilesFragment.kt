package kittoku.osc.fragment

import android.app.Activity
import android.content.SharedPreferences
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.ViewGroup
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.preference.Preference
import androidx.preference.PreferenceCategory
import androidx.preference.PreferenceFragmentCompat
import kittoku.osc.R
import kittoku.osc.control.SmartConnect
import kittoku.osc.control.qualityScore
import kittoku.osc.activity.BlankActivity
import kittoku.osc.activity.EXTRA_AUTO_CONNECT
import kittoku.osc.activity.EXTRA_KEY_TYPE
import kittoku.osc.activity.BLANK_ACTIVITY_TYPE_FINDER
import kittoku.osc.extension.removeTemporaryPreferences
import kittoku.osc.preference.*
import kotlinx.serialization.json.Json
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executors

/** Saved profiles retain all of the upstream settings, not just the fields shown here. */
internal class ProfilesFragment : PreferenceFragmentCompat() {
    private lateinit var prefs: SharedPreferences
    private val executor = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private val results = mutableMapOf<String, String>()
    private val finderLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) {
            requireActivity().setResult(Activity.RESULT_OK)
            requireActivity().finish()
        } else if (isAdded) refresh()
    }
    private val importList = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            val resolver = requireContext().contentResolver
            executor.execute {
                try {
                    val bytes = resolver.openInputStream(uri)?.use { input ->
                        // Reject oversized files rather than partially importing a list.
                        val output = java.io.ByteArrayOutputStream()
                        val buffer = ByteArray(8192)
                        while (true) {
                            val n = input.read(buffer)
                            if (n < 0) break
                            if (output.size() + n > 1_048_576) throw IllegalArgumentException("File exceeds 1 MiB")
                            output.write(buffer, 0, n)
                        }
                        output.toByteArray()
                    } ?: throw IllegalArgumentException("Could not open file")
                    val endpoints = bytes.toString(Charsets.UTF_8).lineSequence().mapNotNull { parseEndpoint(it) }.distinct().take(201).toList()
                    main.post {
                        if (isAdded) {
                            if (endpoints.size > 200) {
                                Toast.makeText(requireContext(), "List exceeds 200 unique endpoints; split the file first", Toast.LENGTH_LONG).show()
                            } else {
                                confirmImport(endpoints)
                            }
                        }
                    }
                } catch (_: Exception) {
                    main.post { if (isAdded) Toast.makeText(requireContext(), "Could not read list (maximum 1 MiB)", Toast.LENGTH_LONG).show() }
                }
            }
        }
    }

    override fun onCreatePreferences(savedInstanceState: Bundle?, rootKey: String?) {
        setPreferencesFromResource(R.xml.blank_preference, rootKey)
        prefs = preferenceManager.sharedPreferences!!
        refresh()
    }

    private fun refresh() {
        preferenceScreen.removeAll()

        val actions = PreferenceCategory(requireContext()).apply { title = "Find servers" }
        preferenceScreen.addPreference(actions)

        actions.addPreference(action(
            title = "Add configuration",
            summary = "Start from the current settings",
            icon = R.drawable.ic_bolt_24,
        ) { editProfile(null) })

        actions.addPreference(action(
            title = "Import endpoint list",
            summary = "Choose a text file; nothing is tested automatically",
            icon = R.drawable.ic_download_24,
        ) { importList.launch(arrayOf("text/*", "application/octet-stream")) })

        actions.addPreference(action(
            title = "Find fastest from sources",
            summary = "Manage list URLs and compare TCP connection times",
            icon = R.drawable.ic_speed_24,
        ) {
            finderLauncher.launch(
                Intent(requireContext(), BlankActivity::class.java)
                    .putExtra(EXTRA_KEY_TYPE, BLANK_ACTIVITY_TYPE_FINDER)
            )
        })

        val stats = SmartConnect.readStats(prefs)
        val names = prefs.all.keys.filter { it.startsWith(PROFILE_KEY_HEADER) }
        val ranked = names.sortedWith(
            compareByDescending<String> {
                stats.endpoints[it.removePrefix(PROFILE_KEY_HEADER)]?.qualityScore() ?: 0.0
            }.thenBy { it }
        )

        val best = SmartConnect.bestSavedProfile(prefs)
        if (best != null) {
            val bestName = best.first.removePrefix(PROFILE_KEY_HEADER)
            val smart = PreferenceCategory(requireContext()).apply { title = "Smart connect" }
            preferenceScreen.addPreference(smart)

            smart.addPreference(action(
                title = "Connect to best configuration",
                summary = "Ranks saved configurations by success rate and handshake time: $bestName",
                icon = R.drawable.ic_speed_24,
            ) { applyBest(bestName, best.second) })
        }

        val saved = PreferenceCategory(requireContext()).apply { title = "Saved configurations" }
        preferenceScreen.addPreference(saved)

        if (ranked.isEmpty()) {
            saved.addPreference(Preference(requireContext()).apply {
                title = "No saved configurations"
                summary = "Add one here or save your current settings from the menu."
                isSelectable = false
            })
        }

        ranked.forEach { key ->
            val value = prefs.all[key]
            val profile = (value as? String)?.let { deserializeProfile(it) }
            val name = key.removePrefix(PROFILE_KEY_HEADER)
            val stat = stats.endpoints[name]

            saved.addPreference(Preference(requireContext()).apply {
                title = name
                icon = ContextCompat.getDrawable(requireContext(), R.drawable.ic_shield_24)
                summary = if (profile == null) "Invalid profile" else {
                    val host = profile.stringSetting[OscPrefKey.HOME_HOSTNAME.name].orEmpty()
                    val port = profile.intSetting[OscPrefKey.SSL_PORT.name] ?: 443
                    val quality = when {
                        stat == null || stat.attempts == 0 -> ""
                        stat.successes == 0 -> " · never connected"
                        else -> buildString {
                            append(" · ")
                            append("${stat.successes}/${stat.attempts} ok")
                            if (stat.bestHandshakeMs > 0) append(" · ${stat.bestHandshakeMs} ms")
                        }
                    }
                    "$host:$port$quality" + (results[key]?.let { " · $it" } ?: "")
                }
                setOnPreferenceClickListener { if (profile != null) actions(key, profile); true }
            })
        }
    }

    private fun action(title: String, summary: String, icon: Int, block: () -> Unit): Preference =
        Preference(requireContext()).apply {
            this.title = title
            this.summary = summary
            this.icon = ContextCompat.getDrawable(requireContext(), icon)
            setOnPreferenceClickListener { block(); true }
        }

    /** Applies the smartest saved configuration and reports back so Home can connect. */
    private fun applyBest(name: String, profile: Profile) {
        if (prefs.getBoolean(OscPrefKey.ROOT_STATE.name, false) || prefs.getBoolean(OscPrefKey.HOME_CONNECTOR.name, false)) {
            Toast.makeText(requireContext(), "Disconnect the VPN before applying a configuration", Toast.LENGTH_LONG).show()
            return
        }

        importProfile(profile, prefs)
        prefs.edit().putString(ACTIVE_PROFILE_KEY, name).apply()
        requireActivity().setResult(Activity.RESULT_OK)
        requireActivity().finish()
    }

    private fun confirmImport(endpoints: List<Endpoint>) {
        if (endpoints.isEmpty()) {
            Toast.makeText(requireContext(), "No valid endpoints found", Toast.LENGTH_LONG).show()
            return
        }
        AlertDialog.Builder(requireContext())
            .setTitle("Import endpoint list?")
            .setMessage("Found ${endpoints.size} unique endpoints. Existing configurations will not be overwritten. Imported entries use default settings and contain no credentials. Only test servers you are authorized to contact.")
            .setPositiveButton("Import") { _, _ ->
                val editor = prefs.edit()
                var added = 0
                endpoints.forEach { endpoint ->
                    val key = PROFILE_KEY_HEADER + endpoint.displayName
                    if (!prefs.contains(key)) {
                        val profile = Profile().apply {
                            stringSetting[OscPrefKey.HOME_HOSTNAME.name] = endpoint.host
                            intSetting[OscPrefKey.SSL_PORT.name] = endpoint.port
                        }
                        editor.putString(key, Json.encodeToString(Profile.serializer(), profile))
                        added++
                    }
                }
                editor.apply()
                refresh()
                Toast.makeText(requireContext(), "Imported $added configurations", Toast.LENGTH_LONG).show()
            }.setNegativeButton("Cancel", null).show()
    }

    private fun actions(key: String, profile: Profile) {
        AlertDialog.Builder(requireContext())
            // AlertDialog shows either a message or a list, not both. A message here
            // hides the actions on AppCompat, making it impossible to apply a profile.
            .setTitle(key.removePrefix(PROFILE_KEY_HEADER))
            .setItems(arrayOf("Connect", "Apply as primary", "Edit", "Test connection", "Delete")) { _, which ->
                when (which) {
                    0 -> applyProfile(key, profile, connect = true)
                    1 -> applyProfile(key, profile, connect = false)
                    2 -> editProfile(key)
                    3 -> testConnection(key, profile)
                    4 -> AlertDialog.Builder(requireContext()).setMessage("Delete this configuration?")
                        .setPositiveButton("Delete") { _, _ -> prefs.edit().remove(key).apply(); results.remove(key); refresh() }
                        .setNegativeButton("Cancel", null).show()
                }
            }.setNegativeButton("Cancel", null).show()
    }

    private fun applyProfile(key: String, profile: Profile, connect: Boolean) {
        if (prefs.getBoolean(OscPrefKey.ROOT_STATE.name, false) || prefs.getBoolean(OscPrefKey.HOME_CONNECTOR.name, false)) {
            Toast.makeText(requireContext(), "Disconnect the VPN before applying a configuration", Toast.LENGTH_LONG).show()
            return
        }

        importProfile(profile, prefs)
        prefs.edit().putString(ACTIVE_PROFILE_KEY, key.removePrefix(PROFILE_KEY_HEADER)).apply()

        requireActivity().setResult(
            Activity.RESULT_OK,
            Intent().putExtra(EXTRA_AUTO_CONNECT, connect),
        )
        requireActivity().finish()
    }

    private fun editProfile(key: String?) {
        val profile = if (key == null) deserializeProfile(serializeProfile(prefs)) else
            prefs.getString(key, null)?.let { deserializeProfile(it) }
        if (profile == null) return
        val layout = LinearLayout(requireContext()).apply {
            orientation = LinearLayout.VERTICAL
            val padding = (24 * resources.displayMetrics.density).toInt()
            setPadding(padding, 0, padding, 0)
        }
        fun field(hintText: String, value: String, type: Int = InputType.TYPE_CLASS_TEXT): EditText {
            val input = EditText(requireContext()).apply { hint = hintText; inputType = type; setText(value); setSingleLine() }
            layout.addView(input, ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            return input
        }
        val name = field("Name", key?.removePrefix(PROFILE_KEY_HEADER).orEmpty())
        val host = field("Hostname or IP address", profile.stringSetting[OscPrefKey.HOME_HOSTNAME.name].orEmpty())
        val port = field("Port", (profile.intSetting[OscPrefKey.SSL_PORT.name] ?: 443).toString(), InputType.TYPE_CLASS_NUMBER)
        val username = field("Username", profile.stringSetting[OscPrefKey.HOME_USERNAME.name].orEmpty())
        val password = field("Password", profile.stringSetting[OscPrefKey.HOME_PASSWORD.name].orEmpty(), InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        val dialog = AlertDialog.Builder(requireContext()).setTitle(if (key == null) "Add configuration" else "Edit configuration")
            .setView(layout).setPositiveButton("Save", null).setNegativeButton("Cancel", null).create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val newName = name.text.toString().trim()
                val newHost = host.text.toString().trim()
                val newPort = port.text.toString().toIntOrNull()
                val newKey = PROFILE_KEY_HEADER + newName
                when {
                    newName.isEmpty() -> name.error = "Name is required"
                    newHost.isEmpty() || newHost.any { it.isWhitespace() || it == '/' } -> host.error = "Enter a hostname or IP address"
                    newPort == null || newPort !in 1..65535 -> port.error = "Enter a port from 1 to 65535"
                    newKey != key && prefs.contains(newKey) -> name.error = "Name already exists"
                    else -> {
                        profile.stringSetting[OscPrefKey.HOME_HOSTNAME.name] = newHost.removeSurrounding("[", "]")
                        profile.stringSetting[OscPrefKey.HOME_USERNAME.name] = username.text.toString()
                        profile.stringSetting[OscPrefKey.HOME_PASSWORD.name] = password.text.toString()
                        profile.intSetting[OscPrefKey.SSL_PORT.name] = newPort
                        prefs.edit().apply { if (key != null && key != newKey) remove(key); putString(newKey, Json.encodeToString(Profile.serializer(), profile)); apply() }
                        if (key != null) results.remove(key)
                        dialog.dismiss(); refresh()
                    }
                }
            }
        }
        dialog.show()
    }

    private fun testConnection(key: String, profile: Profile) {
        val host = profile.stringSetting[OscPrefKey.HOME_HOSTNAME.name].orEmpty().removeSurrounding("[", "]")
        val port = profile.intSetting[OscPrefKey.SSL_PORT.name] ?: 443
        if (host.isBlank() || port !in 1..65535) {
            Toast.makeText(requireContext(), "Invalid host or port", Toast.LENGTH_SHORT).show(); return
        }
        results[key] = "Testing…"; refresh()
        executor.execute {
            val result = try {
                val start = System.nanoTime()
                Socket().use { it.connect(InetSocketAddress(host, port), 5000) }
                "TCP reachable · ${(System.nanoTime() - start) / 1_000_000} ms"
            } catch (_: Exception) { "TCP connection failed" }
            main.post { if (isAdded && prefs.contains(key)) { results[key] = result; refresh() } }
        }
    }

    override fun onDestroy() {
        executor.shutdownNow()
        prefs.removeTemporaryPreferences()
        super.onDestroy()
    }
}
