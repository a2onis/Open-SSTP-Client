Open SSTP Client for Windows 11 (x64) — portable
================================================

This is a self-contained build: it needs no .NET runtime and no installer.
Unzip the folder anywhere (for example C:\Tools\OpenSstpWin) and run
OpenSstpWin.exe as administrator.


Quick start
-----------

1. Right-click OpenSstpWin.exe and choose "Run as administrator".
   Administrator rights are needed to create the virtual network adapter.

2. Connect from the command line:

      OpenSstpWin.exe --host vpn.example.com --user myname --interactive

   --interactive prompts for the password without echoing it.
   Press Ctrl+C to disconnect.

3. To keep the settings for next time, store them once:

      OpenSstpWin.exe --set host vpn.example.com
      OpenSstpWin.exe --set username myname
      OpenSstpWin.exe --connect --interactive

   Stored settings live in %LOCALAPPDATA%\OpenSstpWin\settings.json
   The password is stored in plain text there, exactly like the Android app's
   profiles. Clear the "password" field if you prefer to type it each time.


Common options
--------------

  --host <server>          SSTP server (hostname or IP address)
  --port <n>               SSTP port, default 443
  --user <name>            PPP username
  --password <secret>      PPP password (or -i to be prompted)
  --auth <list>            PAP,MSCHAPv2,EAP-MSCHAPv2 (comma separated)
  --insecure               Do not verify the server TLS certificate
  --ca <file.pem>          Extra trusted root certificates
  --mtu <n>                MTU for the tunnel, default 1500
  --no-default-route       Do not route all traffic through the VPN
  --route <prefix>         Add a prefix, for example 10.20.0.0/16 (repeatable)
  --dns off                Ignore the DNS servers the server offers
  --proxy <host:port>      Use an HTTP CONNECT proxy
  --adapter <name>         Wintun adapter name, default OpenSSTP
  --show                   Print the stored settings
  --help                   Full option list


What the program changes on your PC
-----------------------------------

* Creates a Wintun virtual adapter named "OpenSSTP" while connected and removes
  it again on disconnect. Wintun is the signed, open-source driver used by
  WireGuard; wintun.dll ships in this folder.
* Adds routes to the Windows routing table (active store only, so they are not
  kept after a reboot) and removes them on disconnect.
* Sets the adapter's DNS servers and restores your original configuration when
  the tunnel closes.

If the program is killed instead of closed, the routes may remain. Running it
again and disconnecting, or rebooting, clears them.


Troubleshooting
---------------

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
    "Trusted Root Certification Authorities", use --ca server-ca.pem, or accept
    the risk with --insecure.

No internet after connecting
    Look at the log file in %LOCALAPPDATA%\OpenSstpWin\logs. If routes were
    added, check that the VPN server itself is reachable: it must stay routed
    over your normal connection, which this program does automatically.

Logs
    %LOCALAPPDATA%\OpenSstpWin\logs\session-*.log


Licence
-------
MIT. This program is a Windows port of Open-SSTP-Client by kittoku.
Wintun is Copyright (C) WireGuard LLC and is included unmodified; see
WINTUN-LICENSE.txt.
