package kittoku.osc.preference

import java.net.URI
import java.util.Locale

/** An SSTP endpoint, not a network range. Missing ports use the SSTP default. */
internal data class Endpoint(val host: String, val port: Int) {
    val displayName: String get() = if (host.contains(':')) "[$host]:$port" else "$host:$port"
}

internal fun parseEndpoint(raw: String): Endpoint? {
    var line = raw.substringBefore('#').trim().substringAfterLast('|').trim()
    if (line.isEmpty()) return null
    val hasScheme = line.contains("://")
    if (!hasScheme && line.contains('/')) return null // Never treat a CIDR as a single host.
    if (!hasScheme && line.count { it == ':' } > 1 && !line.startsWith("[")) {
        line = "[$line]" // Bare IPv6 has no port.
    }
    val uri = try { URI(if (hasScheme) line else "https://$line") } catch (_: Exception) { return null }
    if (uri.scheme?.lowercase(Locale.ROOT) !in listOf("http", "https")) return null
    if (uri.rawAuthority?.endsWith(":") == true) return null
    val host = uri.host?.removeSurrounding("[", "]")?.lowercase(Locale.ROOT) ?: return null
    if (host.isEmpty() || host.contains('%')) return null
    if (!host.contains(':')) {
        if (host.length > 253 || host.split('.').any {
                it.isEmpty() || it.length > 63 || !it.matches(Regex("[a-z0-9](?:[a-z0-9-]*[a-z0-9])?"))
            }) return null
        if (host.all { it.isDigit() || it == '.' }) {
            val parts = host.split('.')
            if (parts.size != 4 || parts.any { it.toIntOrNull()?.let { n -> n in 0..255 } != true }) return null
        }
    }
    val port = if (uri.port == -1) 443 else uri.port
    if (port !in 1..65535) return null
    return Endpoint(host, port)
}
