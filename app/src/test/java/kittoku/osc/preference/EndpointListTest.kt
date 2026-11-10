package kittoku.osc.preference

import org.junit.Assert.*
import org.junit.Test

class EndpointListTest {
    @Test fun parsesSingleHostsAndLabels() {
        assertEquals(Endpoint("vpn.example.org", 443), parseEndpoint("Vietnam | VPN.EXAMPLE.ORG:443"))
        assertEquals(Endpoint("192.0.2.1", 443), parseEndpoint("192.0.2.1"))
        assertEquals(Endpoint("2001:db8::1", 8443), parseEndpoint("[2001:db8::1]:8443"))
        assertEquals(Endpoint("2001:db8::1", 443), parseEndpoint("2001:db8::1"))
        assertEquals(Endpoint("vpn.example.org", 8443), parseEndpoint("https://user:password@vpn.example.org:8443/path?x=1"))
    }

    @Test fun rejectsRangesAndInvalidEndpoints() {
        listOf("", "# comment", "192.0.2.0/24", "2001:db8::/64", "999.1.1.1", "example.org:0", "example.org:65536", "example.org:", "ftp://example.org", "example.org:abc").forEach {
            assertNull("Should reject $it", parseEndpoint(it))
        }
    }
}
