package kittoku.osc.activity

import android.content.res.Configuration
import android.os.Bundle
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
import androidx.fragment.app.Fragment
import kittoku.osc.R
import kittoku.osc.databinding.ActivityBlankBinding
import kittoku.osc.fragment.AppsFragment
import kittoku.osc.fragment.FinderFragment
import kittoku.osc.fragment.ProfilesFragment
import kittoku.osc.fragment.SaveCertFragment


internal const val BLANK_ACTIVITY_TYPE_PROFILES = 0
internal const val BLANK_ACTIVITY_TYPE_APPS = 1
internal const val BLANK_ACTIVITY_TYPE_SAVE_CERT = 2
internal const val BLANK_ACTIVITY_TYPE_FINDER = 3

internal const val EXTRA_KEY_TYPE = "TYPE"
internal const val EXTRA_KEY_CERT = "CERT"
internal const val EXTRA_KEY_FILENAME = "FILENAME"
internal const val EXTRA_AUTO_CONNECT = "AUTO_CONNECT"

class BlankActivity : AppCompatActivity() {
    private lateinit var binding: ActivityBlankBinding

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val fragment: Fragment
        var screenTitle = getString(R.string.app_name)

        when (intent.extras!!.getInt(EXTRA_KEY_TYPE)) {
            BLANK_ACTIVITY_TYPE_PROFILES -> {
                screenTitle = "Configurations"
                fragment = ProfilesFragment()
            }

            BLANK_ACTIVITY_TYPE_FINDER -> {
                screenTitle = "Find configurations"
                fragment = FinderFragment()
            }

            BLANK_ACTIVITY_TYPE_APPS -> {
                screenTitle = "Allowed/Disallowed Apps"
                fragment = AppsFragment()
            }

            BLANK_ACTIVITY_TYPE_SAVE_CERT -> {
                screenTitle = "Save certificate"
                fragment = SaveCertFragment(intent)
            }

            else -> throw NotImplementedError(intent.toString())
        }

        title = screenTitle

        binding = ActivityBlankBinding.inflate(layoutInflater)
        setContentView(binding.root)

        setSupportActionBar(binding.toolbar)
        supportActionBar?.title = screenTitle
        supportActionBar?.setDisplayHomeAsUpEnabled(true)
        supportActionBar?.setDisplayShowHomeEnabled(true)

        applyEdgeToEdge()

        if (savedInstanceState == null) supportFragmentManager.beginTransaction().also {
            it.replace(R.id.blank, fragment)
            it.commit()
        }
    }

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
            binding.blank.updatePadding(bottom = bars.bottom)

            insets
        }
    }

    override fun onSupportNavigateUp(): Boolean {
        finish()
        return true
    }
}
