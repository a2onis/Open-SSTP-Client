package kittoku.osc.fragment

import android.app.Activity
import android.content.SharedPreferences
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.widget.EditText
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.preference.Preference
import androidx.preference.PreferenceFragmentCompat
import kittoku.osc.R
import kittoku.osc.preference.Endpoint
import kittoku.osc.preference.OscPrefKey
import kittoku.osc.preference.PROFILE_KEY_HEADER
import kittoku.osc.preference.Profile
import kittoku.osc.preference.deserializeProfile
import kittoku.osc.preference.importProfile
import kittoku.osc.preference.parseEndpoint
import kotlinx.serialization.json.Json
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URL
import java.util.concurrent.ExecutorCompletionService
import java.util.concurrent.Executors
import java.util.concurrent.Future
import java.util.concurrent.Callable

private const val SOURCE_KEY = "configuration_source_urls"
private const val MAX_ENDPOINTS = 1000
private val DEFAULT_SOURCES = setOf(
    "https://raw.githubusercontent.com/F0rc3Run/F0rc3Run/refs/heads/main/sstp-configs/sstp_with_country.txt",
    "https://raw.githubusercontent.com/Delta-Kronecker/Vpn-Gate/refs/heads/main/sstp_hosts.txt"
)
private data class TimedEndpoint(val endpoint: Endpoint, val latencyMs: Long)

/** Downloads only on user request; tests only the advertised port of each endpoint. */
internal class FinderFragment : PreferenceFragmentCompat() {
    private lateinit var prefs: SharedPreferences
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private var job: Future<*>? = null
    private var generation = 0
    private var status = "No comparison yet"
    private var results = emptyList<TimedEndpoint>()
    private var busy = false

    override fun onCreatePreferences(savedInstanceState: Bundle?, rootKey: String?) {
        setPreferencesFromResource(R.xml.blank_preference, rootKey)
        prefs = preferenceManager.sharedPreferences!!
        if (!prefs.contains(SOURCE_KEY)) prefs.edit().putStringSet(SOURCE_KEY, DEFAULT_SOURCES).apply()
        refresh()
    }

    private fun sources() = prefs.getStringSet(SOURCE_KEY, emptySet())!!.toSortedSet()
    private fun saveSources(urls: Set<String>) { prefs.edit().putStringSet(SOURCE_KEY, urls.toSet()).apply(); refresh() }

    private fun refresh() {
        if (!isAdded) return
        preferenceScreen.removeAll()
        preferenceScreen.addPreference(Preference(requireContext()).apply {
            title = if (busy) "Cancel comparison" else "Compare sources"
            summary = status
            setOnPreferenceClickListener {
                if (busy) cancel() else confirmScan()
                true
            }
        })
        preferenceScreen.addPreference(Preference(requireContext()).apply {
            title = "Add source URL"
            summary = "HTTPS text lists, one endpoint per line"
            isEnabled = !busy
            setOnPreferenceClickListener { editSource(null); true }
        })
        sources().forEach { url ->
            preferenceScreen.addPreference(Preference(requireContext()).apply {
                title = url
                summary = "Tap to edit or remove"
                isEnabled = !busy
                setOnPreferenceClickListener { sourceActions(url); true }
            })
        }
        results.forEachIndexed { index, result ->
            preferenceScreen.addPreference(Preference(requireContext()).apply {
                title = "${index + 1}. ${result.endpoint.displayName}"
                summary = "TCP connect: ${result.latencyMs} ms · tap to save or apply"
                setOnPreferenceClickListener { resultActions(result.endpoint); true }
            })
        }
    }

    private fun sourceActions(url: String) {
        AlertDialog.Builder(requireContext()).setTitle(url)
            .setItems(arrayOf("Edit URL", "Remove URL")) { _, which ->
                if (which == 0) editSource(url) else { saveSources(sources() - url); results = emptyList(); status = "Sources changed; compare again"; refresh() }
            }.setNegativeButton("Cancel", null).show()
    }

    private fun editSource(old: String?) {
        val input = EditText(requireContext()).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            hint = "https://example.org/list.txt"
            setText(old.orEmpty())
        }
        val dialog = AlertDialog.Builder(requireContext()).setTitle(if (old == null) "Add source" else "Edit source")
            .setView(input).setPositiveButton("Save", null).setNegativeButton("Cancel", null).create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val text = input.text.toString().trim()
                val valid = try {
                    val url = URL(text)
                    url.protocol.equals("https", true) && url.host.isNotBlank() && url.userInfo == null
                } catch (_: Exception) { false }
                if (!valid) input.error = "Enter an HTTPS URL without credentials" else {
                    saveSources((if (old == null) sources() else sources() - old) + text)
                    results = emptyList(); status = "Sources changed; compare again"; refresh()
                    dialog.dismiss()
                }
            }
        }
        dialog.show()
    }

    private fun confirmScan() {
        val urls = sources().toList()
        if (urls.isEmpty()) { Toast.makeText(requireContext(), "Add a source first", Toast.LENGTH_SHORT).show(); return }
        AlertDialog.Builder(requireContext()).setTitle("Compare endpoints?")
            .setMessage("Download ${urls.size} list(s) and attempt a TCP connection to each advertised host and port, up to $MAX_ENDPOINTS unique endpoints. Only test servers you are authorized to contact. TCP timing does not verify SSTP or login.")
            .setPositiveButton("Compare") { _, _ -> scan(urls) }.setNegativeButton("Cancel", null).show()
    }

    private fun scan(urls: List<String>) {
        busy = true; results = emptyList(); status = "Downloading lists…"; refresh()
        val run = ++generation
        job = worker.submit {
            val pool = Executors.newFixedThreadPool(12)
            try {
                val endpoints = linkedSetOf<Endpoint>()
                val failures = mutableListOf<String>()
                urls.forEach { url ->
                    if (Thread.currentThread().isInterrupted) throw InterruptedException()
                    try {
                        download(url).lineSequence().mapNotNull { parseEndpoint(it) }.forEach {
                            endpoints.add(it)
                            if (endpoints.size > MAX_ENDPOINTS) throw IllegalArgumentException("More than $MAX_ENDPOINTS endpoints")
                        }
                    } catch (e: InterruptedException) { throw e }
                    catch (e: Exception) {
                        if (e is IllegalArgumentException && e.message?.startsWith("More than") == true) throw e
                        failures.add(url)
                    }
                }
                if (endpoints.isEmpty()) throw IllegalStateException("No valid endpoints found; ${failures.size} source(s) failed")
                val completion = ExecutorCompletionService<TimedEndpoint?>(pool)
                endpoints.forEach { endpoint -> completion.submit(Callable<TimedEndpoint?> {
                    try {
                        val start = System.nanoTime()
                        Socket().use { it.connect(InetSocketAddress(endpoint.host, endpoint.port), 3000) }
                        TimedEndpoint(endpoint, (System.nanoTime() - start) / 1_000_000)
                    } catch (_: Exception) { null }
                }) }
                val found = mutableListOf<TimedEndpoint>()
                repeat(endpoints.size) { index ->
                    completion.take().get()?.let { found.add(it) }
                    if (index % 10 == 0 || index + 1 == endpoints.size) {
                        val reachable = found.size
                        main.post {
                            if (isAdded && run == generation) { status = "Tested ${index + 1}/${endpoints.size} · reachable $reachable"; refresh() }
                        }
                    }
                }
                val sorted = found.sortedWith(compareBy<TimedEndpoint> { it.latencyMs }.thenBy { it.endpoint.displayName })
                main.post { if (isAdded && run == generation) {
                    results = sorted; busy = false
                    status = "${sorted.size}/${endpoints.size} reachable" + if (failures.isNotEmpty()) " · ${failures.size} source(s) failed" else ""
                    refresh()
                } }
            } catch (e: Exception) {
                main.post { if (isAdded && run == generation) { busy = false; status = e.message ?: "Comparison failed"; refresh() } }
            } finally { pool.shutdownNow() }
        }
    }

    private fun download(address: String): String {
        val connection = URL(address).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = 10000; connection.readTimeout = 10000
            connection.instanceFollowRedirects = false
            if (connection.responseCode != 200) throw IllegalStateException("HTTP ${connection.responseCode}")
            return connection.inputStream.use { input ->
                val output = java.io.ByteArrayOutputStream()
                val buffer = ByteArray(8192)
                while (true) {
                    if (Thread.currentThread().isInterrupted) throw InterruptedException()
                    val n = input.read(buffer)
                    if (n < 0) break
                    if (output.size() + n > 1_048_576) throw IllegalStateException("Source exceeds 1 MiB")
                    output.write(buffer, 0, n)
                }
                output.toString("UTF-8")
            }
        } finally { connection.disconnect() }
    }

    private fun cancel() {
        generation++; job?.cancel(true); busy = false; status = "Comparison cancelled"; refresh()
    }

    private fun resultActions(endpoint: Endpoint) {
        AlertDialog.Builder(requireContext()).setTitle(endpoint.displayName)
            .setItems(arrayOf("Save configuration", "Apply as primary")) { _, which ->
                val key = PROFILE_KEY_HEADER + endpoint.displayName
                val profile = prefs.getString(key, null)?.let { deserializeProfile(it) } ?: Profile().apply {
                    stringSetting[OscPrefKey.HOME_HOSTNAME.name] = endpoint.host
                    intSetting[OscPrefKey.SSL_PORT.name] = endpoint.port
                }
                if (which == 1 && (prefs.getBoolean(OscPrefKey.ROOT_STATE.name, false) || prefs.getBoolean(OscPrefKey.HOME_CONNECTOR.name, false))) {
                    Toast.makeText(requireContext(), "Disconnect the VPN before applying a configuration", Toast.LENGTH_LONG).show()
                    return@setItems
                }
                if (!prefs.contains(key)) prefs.edit().putString(key, Json.encodeToString(Profile.serializer(), profile)).apply()
                if (which == 1) {
                    importProfile(profile, prefs)
                    prefs.edit().putString("active_profile_name", endpoint.displayName).apply()
                    requireActivity().setResult(Activity.RESULT_OK)
                    requireActivity().finish()
                } else Toast.makeText(requireContext(), "Saved; edit credentials in Configurations", Toast.LENGTH_LONG).show()
            }.setNegativeButton("Cancel", null).show()
    }

    override fun onDestroy() {
        generation++; job?.cancel(true); worker.shutdownNow(); super.onDestroy()
    }
}
