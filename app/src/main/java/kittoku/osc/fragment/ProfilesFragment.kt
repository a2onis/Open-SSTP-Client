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
import androidx.preference.Preference
import androidx.preference.PreferenceFragmentCompat
import kittoku.osc.R
import kittoku.osc.activity.BlankActivity
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
        preferenceScreen.addPreference(Preference(requireContext()).apply {
            title = "Add configuration"
            summary = "Start from the current settings"
            setOnPreferenceClickListener { editProfile(null); true }
        })
        preferenceScreen.addPreference(Preference(requireContext()).apply {
            title = "Import endpoint list"
            summary = "Choose a text file; nothing is tested automatically"
            setOnPreferenceClickListener { importList.launch(arrayOf("text/*", "application/octet-stream")); true }
        })
        preferenceScreen.addPreference(Preference(requireContext()).apply {
            title = "Find fastest from sources"
            summary = "Manage list URLs and compare TCP connection times"
            setOnPreferenceClickListener {
                finderLauncher.launch(Intent(requireContext(), BlankActivity::class.java).putExtra(EXTRA_KEY_TYPE, BLANK_ACTIVITY_TYPE_FINDER))
                true
            }
        })
        val entries = prefs.all.filterKeys { it.startsWith(PROFILE_KEY_HEADER) }.toSortedMap()
        if (entries.isEmpty()) {
            preferenceScreen.addPreference(Preference(requireContext()).apply {
                title = "No saved configurations"
                summary = "Add one here or save your current settings from the menu."
                isSelectable = false
            })
        }
        entries.forEach { (key, value) ->
            val profile = (value as? String)?.let { deserializeProfile(it) }
            preferenceScreen.addPreference(Preference(requireContext()).apply {
                title = key.removePrefix(PROFILE_KEY_HEADER)
                summary = if (profile == null) "Invalid profile" else {
                    val host = profile.stringSetting[OscPrefKey.HOME_HOSTNAME.name].orEmpty()
                    val port = profile.intSetting[OscPrefKey.SSL_PORT.name] ?: 443
                    "$host:$port" + (results[key]?.let { " · $it" } ?: "")
                }
                setOnPreferenceClickListener { if (profile != null) actions(key, profile); true }
            })
        }
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
            .setTitle(key.removePrefix(PROFILE_KEY_HEADER))
            .setMessage(summarizeProfile(profile))
            .setItems(arrayOf("Apply as primary", "Edit", "Test connection", "Delete")) { _, which ->
                when (which) {
                    0 -> {
                        if (prefs.getBoolean(OscPrefKey.ROOT_STATE.name, false) || prefs.getBoolean(OscPrefKey.HOME_CONNECTOR.name, false)) {
                            Toast.makeText(requireContext(), "Disconnect the VPN before applying a configuration", Toast.LENGTH_LONG).show()
                            return@setItems
                        }
                        importProfile(profile, prefs)
                        prefs.edit().putString("active_profile_name", key.removePrefix(PROFILE_KEY_HEADER)).apply()
                        requireActivity().setResult(Activity.RESULT_OK)
                        requireActivity().finish()
                    }
                    1 -> editProfile(key)
                    2 -> testConnection(key, profile)
                    3 -> AlertDialog.Builder(requireContext()).setMessage("Delete this configuration?")
                        .setPositiveButton("Delete") { _, _ -> prefs.edit().remove(key).apply(); results.remove(key); refresh() }
                        .setNegativeButton("Cancel", null).show()
                }
            }.setNegativeButton("Cancel", null).show()
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
