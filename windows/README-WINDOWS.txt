Open SSTP Client for Windows 11 (x64) — portable
================================================

This is a self-contained build: it needs no .NET runtime and no installer.
Unzip the folder anywhere (for example C:\Tools\OpenSstpWin) and run
OpenSstpWin.exe as administrator.


Quick start
-----------

1. Double-click OpenSstpWin.exe and approve the administrator prompt. A console
   may briefly appear before the window opens. Administrator rights are needed
   to create the virtual network adapter.

2. Enter the server, port, username and password, then click Connect. Connection
   status and errors appear in the window and in the status bar. Click
   Disconnect or close the window to disconnect and clean up the adapter and
   routes.

3. The server and other settings are saved when you connect. The password is
   saved only if you check "Remember password". Saved passwords and saved
   configurations are plaintext in %LOCALAPPDATA%\OpenSstpWin; protect those
   files accordingly.

The command-line interface is still available from an administrator terminal:

      OpenSstpWin.exe --host vpn.example.com --user myname --interactive
      OpenSstpWin.exe --help


Configurations
--------------

The window has the same profile workflow as the Android app.

* The list at the top of the window shows saved configurations. Choose one and
  click Load, or open Configuration → Manage Configurations… to see the full
  list with the endpoint, username and last connection-test result.
* "Add from current settings…" creates a configuration from everything
  currently in the window, including the advanced settings. The name must be
  unique; saving over an existing name asks first.
* "Edit…" changes the name, server, port, username and password, and has an
  "Advanced settings…" button for all other options. Editing keeps the
  configuration's other settings.
* "Test connection" makes one TCP connection to the host and port and reports
  the elapsed time. It is not an ICMP ping and does not prove that SSTP or the
  credentials work. Only test servers you are authorized to contact.
* "Use selected" applies the configuration to the current window and closes the
  dialog. The VPN must be disconnected, because the Connect button is disabled
  while a session is running.
* The File menu also has Save Profile…, Import Profile… and Export Profile…
  for a single settings JSON file. Export includes the password as plaintext.
  Settings → Reload Defaults restores every default setting.

Import endpoint list
--------------------

In Configurations choose "Import endpoint list…" and select a UTF-8 text file.
Each line may be a hostname, IP address, host:port, bracketed IPv6 address and
port, or an HTTP(S) URL. An optional "country |" label and "#" comments are
ignored. Addresses without a port use SSTP port 443; network ranges such as
CIDR are rejected. Files are limited to 1 MiB and 200 unique endpoints.

Existing configurations with the same endpoint name are not replaced. Imported
profiles use default settings and have no credentials, so edit them before
connecting. Importing a file does not contact any listed host.

For example, download the two lists separately to a computer, then import each
text file in the window:

    curl -fL 'https://raw.githubusercontent.com/F0rc3Run/F0rc3Run/refs/heads/main/sstp-configs/sstp_with_country.txt' -o sstp-with-country.txt
    curl -fL 'https://raw.githubusercontent.com/Delta-Kronecker/Vpn-Gate/refs/heads/main/sstp_hosts.txt' -o sstp-hosts.txt


Find fastest from sources
-------------------------

Open Configuration → Find Fastest from Sources… (also available from the
Configurations window). The two lists above are preconfigured; add, edit or
remove HTTPS source URLs.

Choose "Compare sources" and confirm. The program downloads each list (maximum
1 MiB per source, no redirects), deduplicates at most 1,000 endpoints, and
attempts a TCP connection to each advertised host and port with bounded
concurrency. Successful connections are sorted by elapsed time. The comparison
can be cancelled; source errors are reported. If the combined endpoint limit is
exceeded the comparison stops rather than silently ranking a subset.

Select a result to:

* Save selected as configuration — stores the endpoint with default settings
  and no credentials; edit it in Configurations before connecting.
* Use selected — applies the endpoint and port to the current settings and
  keeps the current credentials and advanced options.

This is TCP connect time (including name resolution), not nmap's ICMP latency,
and it does not establish that SSTP or the credentials work. Results depend on
your network and are not a throughput measurement. Only test endpoints you are
authorized to contact. Disconnect the VPN before applying a configuration.


Advanced settings
-----------------

Settings → Advanced Settings… opens the Windows counterpart of the Android
SETTING tab. OK saves the values to settings.json; Cancel discards them.

SSL / TLS
    TLS version (Default, TLS 1.2, TLS 1.3), verify the server certificate,
    trust only a custom CA bundle (PEM file or folder), custom SNI hostname,
    and a best-effort cipher-suite list. Windows Schannel may ignore explicit
    cipher suites; the connection then continues with the OS defaults.

Proxy
    HTTP CONNECT proxy host, port, username and password.

PPP
    MRU, MTU, authentication timeout, PAP / MS-CHAPv2 / EAP-MS-CHAPv2,
    IPv4 (IPCP), a requested static IPv4 address, and IPv6 (IPv6CP).

DNS
    Request DNS servers from the server, or use custom IPv4 DNS servers.
    Custom DNS queries are kept inside the tunnel with host routes.

Routing
    Default route, private (RFC1918) routes, and additional prefixes.
    Android's app-based rules are not available: the Wintun adapter is
    machine-wide, so every application uses the tunnel.

Reconnection
    Automatically retry after a failure or an unexpected drop, with a chosen
    number of attempts and interval. Clicking Disconnect never triggers a
    retry.

Logging
    Save a session log, choose the log folder, and change the Wintun adapter
    name.

The menu Settings → Theme offers System default, Light and Dark. The choice is
saved in settings.json and also applies to the dialogs.


Command-line options
--------------------

Connection:
  --host <server>          SSTP server (hostname or IP address)
  --port <n>               SSTP port, default 443
  --user <name>            PPP username
  --password <secret>      PPP password (or -i to be prompted)
  --auth <list>            PAP,MSCHAPv2,EAP-MSCHAPv2 (comma separated)
  --auth-timeout <s>       PPP authentication timeout, default 15
  --tls <mode>             DEFAULT, TLS12 or TLS13
  --insecure               Do not verify the server TLS certificate
  --ca <file-or-dir>       Trust only these certificates (PEM/CRT/CER)
  --sni <hostname>         Send a custom TLS server name
  --cipher-suites <list>   Best-effort cipher-suite restriction
  --mtu <n> / --mru <n>    Tunnel MTU/MRU, default 1500
  --ipv4 on|off            Enable IPCP, default on
  --ipv6 on|off            Enable IPv6CP, default off
  --static-ip <address>    Ask the server for a specific IPv4 address
  --no-default-route       Do not route all traffic through the VPN
  --route <prefix>         Add a prefix, for example 10.20.0.0/16 (repeatable)
  --private-routes on|off  Route RFC1918 networks, default on
  --custom-routes on|off   Add the --route prefixes, default on
  --dns on|off             Request DNS servers from the server
  --custom-dns <list>      Use these IPv4 DNS servers instead
  --proxy <host:port>      Use an HTTP CONNECT proxy
  --proxy-user <name>      Proxy credentials
  --proxy-password <pw>    Proxy credentials
  --reconnect <n>          Retry a failed/dropped connection n times
  --reconnect-interval <s> Seconds between retries, default 10
  --adapter <name>         Wintun adapter name, default OpenSSTP
  --log-dir <folder>       Where session logs are written
  --no-log                 Do not write a session log
  --theme <mode>           System, Light or Dark
  --interactive, -i        Prompt for the password
  --show                   Print the stored settings
  --set <key> <value>      Persist a setting for future runs
  --help, --version

Configurations:
  --list-profiles          List saved configurations
  --profile <name>         Load a saved configuration for this run
  --save-profile <name>    Save the current settings as a configuration
  --import-profile <file>  Import a settings JSON profile
  --export-profile <file>  Export the current settings (plaintext password)
  --import-endpoints <file> Import an endpoint list (max 200 entries)
  --find-fastest           Compare the configured HTTPS sources over TCP
  --source <https-url>     Source list for --find-fastest (repeatable)

Examples:

      OpenSstpWin.exe --list-profiles
      OpenSstpWin.exe --profile Work --interactive
      OpenSstpWin.exe --host vpn.example.com --user me --reconnect 5 --tls TLS12
      OpenSstpWin.exe --find-fastest
      OpenSstpWin.exe --import-endpoints sstp-hosts.txt
      OpenSstpWin.exe --set theme Dark

Press Ctrl+C to disconnect. A command-line run does not save the password
unless it was already stored or --set password is used.


What the program changes on your PC
-----------------------------------

* Creates a Wintun virtual adapter named "OpenSSTP" while connected and removes
  it again on disconnect. Wintun is the signed, open-source driver used by
  WireGuard; wintun.dll ships in this folder.
* Sets the adapter MTU and IPv4/IPv6 addresses.
* Adds routes to the Windows routing table (active store only, so they are not
  kept after a reboot) and removes them on disconnect.
* Sets the adapter's DNS servers and restores your original configuration when
  the tunnel closes.

If the program is killed instead of closed, the routes may remain. Running it
again and disconnecting, or rebooting, clears them.


Files
-----

  settings.json   %LOCALAPPDATA%\OpenSstpWin\settings.json
  profiles.json   %LOCALAPPDATA%\OpenSstpWin\profiles.json
  logs            %LOCALAPPDATA%\OpenSstpWin\logs\session-*.log

Credentials and proxy passwords are stored as plaintext in settings.json and
profiles.json. Exported profiles also contain the plaintext password.


Troubleshooting
---------------

"No window appears"
    Extract the entire ZIP before starting OpenSstpWin.exe, and approve the
    Windows administrator prompt. Run it from a terminal with --help to see
    command-line output.

"Administrator rights are required"
    Start the program with "Run as administrator".

"wintun.dll is missing"
    Keep wintun.dll in the same folder as OpenSstpWin.exe.

"Cannot reach the server"
    Check the host and port, and that outbound TCP to that port is allowed.

"Authentication failed"
    Verify the username and password. Servers usually offer MS-CHAPv2; PAP is
    also tried by default. Try --auth MSCHAPv2 if your server is picky.

TLS handshake failed
    The server certificate is not trusted. Install the server's CA in
    "Trusted Root Certification Authorities", use Advanced Settings → trust a
    custom CA bundle (or --ca server-ca.pem), or accept the risk with
    --insecure.

"More than 1000 endpoints"
    Find fastest from sources ranks a complete list; remove a source or split
    it. Endpoint-list import has a separate limit of 200 entries.

No internet after connecting
    Look at the log file in %LOCALAPPDATA%\OpenSstpWin\logs. If routes were
    added, check that the VPN server itself is reachable: it must stay routed
    over your normal connection, which this program does automatically.

A configuration cannot be applied
    Disconnect first. Use selected / Load is disabled while a session is
    running.

Logs
    %LOCALAPPDATA%\OpenSstpWin\logs\session-*.log


Licence
-------
MIT. This program is a Windows port of Open-SSTP-Client by kittoku.
Wintun is Copyright (C) WireGuard LLC and is included unmodified; see
WINTUN-LICENSE.txt.
