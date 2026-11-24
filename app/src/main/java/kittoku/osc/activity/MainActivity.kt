package kittoku.osc.activity

import android.Manifest
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.os.Build
import android.os.Bundle
import android.text.InputType
import android.view.Menu
import android.view.MenuInflater
import android.view.MenuItem
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
import androidx.fragment.app.Fragment
import androidx.preference.EditTextPreference
import androidx.preference.PreferenceFragmentCompat
import androidx.preference.PreferenceGroup
import androidx.preference.PreferenceManager
import androidx.preference.forEach
import androidx.viewpager2.adapter.FragmentStateAdapter
import com.google.android.material.tabs.TabLayoutMediator
import kittoku.osc.BuildConfig
import kittoku.osc.R
import kittoku.osc.control.SmartConnect
import kittoku.osc.databinding.ActivityMainBinding
import kittoku.osc.extension.firstEditText
import kittoku.osc.extension.sum
import kittoku.osc.fragment.HomeFragment
import kittoku.osc.fragment.SettingFragment
import kittoku.osc.preference.ACTIVE_PROFILE_KEY
import kittoku.osc.preference.OscPrefKey
import kittoku.osc.preference.PROFILE_KEY_HEADER
import kittoku.osc.preference.accessor.getStringPrefValue
import kittoku.osc.preference.custom.OscPreference
import kittoku.osc.preference.deserializeProfile
import kittoku.osc.preference.importProfile
import kittoku.osc.preference.serializeProfile
import java.io.BufferedInputStream
import java.io.BufferedOutputStream


class MainActivity : AppCompatActivity() {
    private lateinit var prefs: SharedPreferences
    private lateinit var binding: ActivityMainBinding

    private lateinit var homeFragment: HomeFragment
    private lateinit var settingFragment: SettingFragment

    private val dialogResource: Int by lazy { EditTextPreference(this).dialogLayoutResource }

    private val profileLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode != RESULT_OK) {
            return@registerForActivityResult
        }

        updatePreferenceView()

        if (result.data?.getBooleanExtra(EXTRA_AUTO_CONNECT, false) == true) {
            val home = (supportFragmentManager.findFragmentByTag("f0") as? HomeFragment) ?: homeFragment
            home.connectNow()
        }
    }

    private val importLauncher = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        uri?.also {
            val profile = contentResolver.openInputStream(it)?.let { stream ->
                BufferedInputStream(stream).let {
                    deserializeProfile(it.reader(Charsets.UTF_8).readText())
                }
            }

            if (profile == null) {
                Toast.makeText(this, "IMPORT FAILED", Toast.LENGTH_SHORT).show()
            } else {
                importProfile(profile,prefs)
                updatePreferenceView()
                Toast.makeText(this, "PROFILE IMPORTED", Toast.LENGTH_SHORT).show()
            }
        }
    }

    private val exportLauncher = registerForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri ->
        uri?.also {
            contentResolver.openOutputStream(it)?.also { stream ->
                BufferedOutputStream(stream).use {
                    it.write(serializeProfile(prefs).toByteArray(Charsets.UTF_8))
                }
            }

            Toast.makeText(this, "PROFILE EXPORTED", Toast.LENGTH_SHORT).show()
        }
    }

    fun openConfigurations() {
        profileLauncher.launch(Intent(this, BlankActivity::class.java).putExtra(EXTRA_KEY_TYPE, BLANK_ACTIVITY_TYPE_PROFILES))
    }

    fun openSettings() {
        binding.pager.currentItem = 1
    }

    private fun updatePreferenceView() {
        listOf(0 to homeFragment, 1 to settingFragment).forEach { (index, original) ->
            val fragment = (supportFragmentManager.findFragmentByTag("f$index") as? PreferenceFragmentCompat) ?: original
            if (fragment.isAdded) {
                val preferenceGroups = mutableListOf<PreferenceGroup>(fragment.preferenceScreen)

                while (preferenceGroups.isNotEmpty()) {
                    preferenceGroups.removeAt(0).forEach {
                        if (it is OscPreference) {
                            it.updateView()
                        }

                        if (it is PreferenceGroup) {
                            preferenceGroups.add(it)
                        }
                    }
                }
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        AppCompatDelegate.setDefaultNightMode(PreferenceManager.getDefaultSharedPreferences(this).getInt("app_theme", AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM))
        super.onCreate(savedInstanceState)

        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        setSupportActionBar(binding.toolbar)
        supportActionBar?.title = getString(R.string.app_name)
        supportActionBar?.subtitle = "v${BuildConfig.VERSION_NAME}"

        applyEdgeToEdge()

        prefs = PreferenceManager.getDefaultSharedPreferences(this)
        homeFragment = HomeFragment()
        settingFragment = SettingFragment()

        object : FragmentStateAdapter(this) {
            override fun getItemCount() = 2

            override fun createFragment(position: Int): Fragment {
                return when (position) {
                    0 -> homeFragment
                    1 -> settingFragment
                    else -> throw NotImplementedError(position.toString())
                }
            }
        }.also {
            binding.pager.adapter = it
        }

        TabLayoutMediator(binding.tabBar, binding.pager) { tab, position ->
            when (position) {
                0 -> {
                    tab.setText(R.string.tab_home)
                    tab.setIcon(R.drawable.ic_home_24)
                }

                1 -> {
                    tab.setText(R.string.tab_settings)
                    tab.setIcon(R.drawable.ic_tune_24)
                }

                else -> throw NotImplementedError(position.toString())
            }
        }.attach()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
            }
        }
    }

    /** Draws behind the system bars and pads the app bar and content instead. */
    private fun applyEdgeToEdge() {
        WindowCompat.setDecorFitsSystemWindows(window, false)

        val isNight = (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) ==
            Configuration.UI_MODE_NIGHT_YES
        WindowCompat.getInsetsController(window, window.decorView).apply {
            isAppearanceLightStatusBars = !isNight
            isAppearanceLightNavigationBars = !isNight
        }

        ViewCompat.setOnApplyWindowInsetsListener(binding.root) { view, insets ->
            val bars = insets.getInsets(
                WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout()
            )

            view.updatePadding(left = bars.left, right = bars.right)
            binding.appBar.updatePadding(top = bars.top)
            binding.pager.updatePadding(bottom = bars.bottom)

            insets
        }
    }

    override fun onCreateOptionsMenu(menu: Menu?): Boolean {
        MenuInflater(this).inflate(R.menu.home_menu, menu)

        return true
    }

    override fun onOptionsItemSelected(item: MenuItem): Boolean {
        when (item.itemId) {
            R.id.load_profile -> {
                openConfigurations()
            }

            R.id.save_profile -> showSaveDialog()

            R.id.import_profile -> importLauncher.launch(arrayOf("application/json"))

            R.id.export_profile -> showExportDialog()

            R.id.reload_defaults -> showReloadDialog()

            R.id.app_theme -> showThemeDialog()

            R.id.connection_quality -> showQualityDialog()
        }

        return true
    }

    private fun showThemeDialog() {
        val modes = intArrayOf(AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM, AppCompatDelegate.MODE_NIGHT_NO, AppCompatDelegate.MODE_NIGHT_YES)
        val selected = modes.indexOf(prefs.getInt("app_theme", AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM)).coerceAtLeast(0)
        AlertDialog.Builder(this).setTitle(R.string.menu_theme)
            .setSingleChoiceItems(arrayOf("System default", "Light", "Dark"), selected) { dialog, which ->
                prefs.edit().putInt("app_theme", modes[which]).apply()
                dialog.dismiss()
                AppCompatDelegate.setDefaultNightMode(modes[which])
            }.setNegativeButton(R.string.action_cancel, null).show()
    }

    private fun showQualityDialog() {
        val summary = SmartConnect.statsSummary(prefs)

        AlertDialog.Builder(this)
            .setTitle(R.string.dialog_quality_title)
            .setMessage(summary)
            .setPositiveButton(R.string.action_close, null)
            .setNeutralButton(R.string.action_reset) { _, _ ->
                SmartConnect.resetLearned(prefs)
                Toast.makeText(this, R.string.toast_stats_reset, Toast.LENGTH_SHORT).show()
                updatePreferenceView()
            }
            .show()
    }

    private fun showSaveDialog() {
        val inflated = layoutInflater.inflate(dialogResource, null)
        val editText = inflated.firstEditText()

        val hostname = getStringPrefValue(OscPrefKey.HOME_HOSTNAME, prefs)

        editText.inputType = InputType.TYPE_CLASS_TEXT
        editText.hint = hostname
        editText.requestFocus()

        AlertDialog.Builder(this).also {
            it.setView(inflated)
            it.setMessage(sum(
                "Enter the profile's name.\n",
                "If blank, the hostname will be used.\n",
                "If duplicated, the profile will be overwritten."
            ))

            it.setPositiveButton("SAVE") { _, _ ->
                val name = editText.text.ifEmpty { hostname }
                prefs.edit().also { editor ->
                    editor.putString(PROFILE_KEY_HEADER + name, serializeProfile(prefs))
                    editor.putString(ACTIVE_PROFILE_KEY, name)
                    editor.apply()
                }

                Toast.makeText(this, "PROFILE SAVED", Toast.LENGTH_SHORT).show()
            }

            it.setNegativeButton("CANCEL") { _, _ -> }

            it.show()
        }
    }

    private fun showExportDialog() {
        val filename = getStringPrefValue(OscPrefKey.HOME_HOSTNAME, prefs) + ".json"

        AlertDialog.Builder(this).also {
            it.setMessage(
                "Password will be also exported as plain text. If you don't want that, blank Password before exporting."
            )

            it.setPositiveButton("PROCEED") { _, _ ->
                exportLauncher.launch(filename)
            }

            it.setNegativeButton("CANCEL") { _, _ -> }

            it.show()
        }
    }

    private fun showReloadDialog() {
        AlertDialog.Builder(this).also {
            it.setMessage("Are you sure to reload the default settings?")

            it.setPositiveButton("YES") { _, _ ->
                importProfile(null, prefs)
                prefs.edit().remove(ACTIVE_PROFILE_KEY).apply()

                updatePreferenceView()

                Toast.makeText(this, "DEFAULTS RELOADED", Toast.LENGTH_SHORT).show()
            }

            it.setNegativeButton("NO") { _, _ -> }

            it.show()
        }
    }
}
