using System.Reflection;
using System.Text;
using OpenSstpWin;
using OpenSstpWin.Core;
using OpenSstpWin.Interop;

if (args.Length == 0)
{
    DesktopApp.Run();
    return 0;
}

try
{
    return await App.RunAsync(args).ConfigureAwait(false);
}
catch (Exception exception)
{
    Console.Error.WriteLine("Error: " + exception.Message);
    return 1;
}

namespace OpenSstpWin
{
    /// <summary>
    /// Command line front end. Configuration lives in
    /// %LOCALAPPDATA%\OpenSstpWin\settings.json but every field can be overridden
    /// per connection with command line options.
    /// </summary>
    internal static class App
    {
        private static readonly object LogLock = new();
        private static StreamWriter? _logWriter;
        private static VpnSession? _session;
        private static volatile bool _stopRequested;

        public static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var settings = Settings.Load();
            var interactive = false;
            var mode = "connect";
            var profileName = string.Empty;
            var filePath = string.Empty;
            var customSources = new List<string>();

            try
            {
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    switch (arg)
                    {
                        case "--help" or "-h" or "/?":
                            PrintUsage();
                            return 0;
                        case "--version" or "-V":
                            Console.WriteLine("Open SSTP Client for Windows " + Version);
                            return 0;
                        case "--connect" or "-c":
                            mode = "connect";
                            break;
                        case "--disconnect":
                            mode = "disconnect";
                            break;
                        case "--interactive" or "-i":
                            interactive = true;
                            break;
                        case "--list-profiles":
                            mode = "list-profiles";
                            break;
                        case "--profile" when i + 1 < args.Length:
                        {
                            var store = ProfileStore.CreateDefault();
                            var profile = store.Find(args[++i]);
                            if (profile is null)
                            {
                                Console.Error.WriteLine("Unknown configuration: " + args[i]);
                                return 1;
                            }

                            settings.CopyFrom(profile.Settings);
                            break;
                        }

                        case "--save-profile" when i + 1 < args.Length:
                            mode = "save-profile";
                            profileName = args[++i];
                            break;
                        case "--import-profile" when i + 1 < args.Length:
                            mode = "import-profile";
                            filePath = args[++i];
                            break;
                        case "--export-profile" when i + 1 < args.Length:
                            mode = "export-profile";
                            filePath = args[++i];
                            break;
                        case "--import-endpoints" when i + 1 < args.Length:
                            mode = "import-endpoints";
                            filePath = args[++i];
                            break;
                        case "--find-fastest":
                            mode = "find-fastest";
                            break;
                        case "--source" when i + 1 < args.Length:
                            customSources.Add(args[++i]);
                            break;
                        case "--set" when i + 2 < args.Length:
                            ApplySetting(settings, args[i + 1], args[i + 2]);
                            i += 2;
                            settings.Normalize();
                            settings.Save();
                            Console.WriteLine($"{args[i - 1]} saved to {SettingsPath}");
                            return 0;
                        case "--show":
                            PrintSettings(settings);
                            return 0;
                        case "--host" when i + 1 < args.Length:
                            settings.Host = args[++i];
                            break;
                        case "--port" when i + 1 < args.Length:
                            settings.Port = ParseInt(args[++i]);
                            break;
                        case "--user" or "--username" when i + 1 < args.Length:
                            settings.Username = args[++i];
                            break;
                        case "--password" when i + 1 < args.Length:
                            settings.Password = args[++i];
                            break;
                        case "--auth" when i + 1 < args.Length:
                            settings.AuthProtocols = args[++i]
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .ToList();
                            break;
                        case "--tls" when i + 1 < args.Length:
                            settings.TlsVersion = TlsModes.Normalize(args[++i]);
                            break;
                        case "--insecure":
                            settings.VerifyServerCertificate = false;
                            break;
                        case "--sni" when i + 1 < args.Length:
                            settings.UseCustomSni = true;
                            settings.CustomSniHostname = args[++i];
                            break;
                        case "--ca" when i + 1 < args.Length:
                            settings.UseCustomCa = true;
                            settings.CaBundlePath = args[++i];
                            break;
                        case "--cipher-suites" when i + 1 < args.Length:
                            settings.UseCipherSuites = true;
                            settings.CipherSuites = SplitList(args[++i]).ToList();
                            break;
                        case "--no-default-route":
                            settings.AddDefaultRoute = false;
                            break;
                        case "--default-route":
                            settings.AddDefaultRoute = true;
                            break;
                        case "--route" when i + 1 < args.Length:
                            settings.CustomRoutes.Add(args[++i]);
                            break;
                        case "--private-routes" when i + 1 < args.Length:
                            settings.RoutePrivateNetworks = ParseBool(args[++i]);
                            break;
                        case "--custom-routes" when i + 1 < args.Length:
                            settings.AddCustomRoutes = ParseBool(args[++i]);
                            break;
                        case "--adapter" when i + 1 < args.Length:
                            settings.AdapterName = args[++i];
                            break;
                        case "--dns" when i + 1 < args.Length:
                            settings.RequestDns = ParseBool(args[++i]);
                            break;
                        case "--custom-dns" when i + 1 < args.Length:
                            settings.UseCustomDns = true;
                            settings.CustomDnsAddress = args[++i];
                            break;
                        case "--mtu" when i + 1 < args.Length:
                            settings.Mtu = ParseInt(args[++i]);
                            break;
                        case "--mru" when i + 1 < args.Length:
                            settings.Mru = ParseInt(args[++i]);
                            break;
                        case "--auth-timeout" when i + 1 < args.Length:
                            settings.AuthTimeoutSeconds = ParseInt(args[++i]);
                            break;
                        case "--ipv4" when i + 1 < args.Length:
                            settings.EnableIpv4 = ParseBool(args[++i]);
                            break;
                        case "--ipv6" when i + 1 < args.Length:
                            settings.EnableIpv6 = ParseBool(args[++i]);
                            break;
                        case "--static-ip" when i + 1 < args.Length:
                            settings.RequestStaticIpv4 = true;
                            settings.StaticIpv4Address = args[++i];
                            break;
                        case "--proxy" when i + 1 < args.Length:
                        {
                            var parts = args[++i].Split(':');
                            settings.UseProxy = true;
                            settings.ProxyHost = parts[0];
                            settings.ProxyPort = parts.Length > 1 ? ParseInt(parts[1]) : 8080;
                            break;
                        }

                        case "--proxy-user" when i + 1 < args.Length:
                            settings.ProxyUsername = args[++i];
                            break;
                        case "--proxy-password" when i + 1 < args.Length:
                            settings.ProxyPassword = args[++i];
                            break;
                        case "--reconnect" when i + 1 < args.Length:
                        {
                            var count = ParseInt(args[++i]);
                            settings.EnableReconnection = count > 0;
                            settings.ReconnectionCount = count;
                            break;
                        }

                        case "--reconnect-interval" when i + 1 < args.Length:
                            settings.ReconnectionIntervalSeconds = ParseInt(args[++i]);
                            break;
                        case "--log-dir" when i + 1 < args.Length:
                            settings.LogFolder = args[++i];
                            break;
                        case "--no-log":
                            settings.SaveLog = false;
                            break;
                        case "--theme" when i + 1 < args.Length:
                            settings.AppTheme = AppThemes.Normalize(args[++i]);
                            break;
                        case "--save" when i + 1 < args.Length:
                            ApplySetting(settings, "host", args[++i]);
                            break;
                        default:
                            if (!arg.StartsWith('-'))
                            {
                                settings.Host = arg;
                                break;
                            }

                            Console.Error.WriteLine("Unknown option: " + arg);
                            PrintUsage();
                            return 1;
                    }
                }
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                Console.Error.WriteLine("Invalid option value: " + exception.Message);
                return 1;
            }

            settings.Normalize();

            switch (mode)
            {
                case "list-profiles":
                    return ListProfiles();
                case "save-profile":
                    return SaveProfile(settings, profileName);
                case "import-profile":
                    return ImportProfile(settings, filePath);
                case "export-profile":
                    return ExportProfile(settings, filePath);
                case "import-endpoints":
                    return ImportEndpoints(filePath);
                case "find-fastest":
                    return await FindFastestAsync(settings, customSources).ConfigureAwait(false);
                case "disconnect":
                    Console.WriteLine("No background VPN service is running. Press Ctrl+C in the window that is connected.");
                    return 0;
            }

            return await ConnectAsync(settings, interactive).ConfigureAwait(false);
        }

        private static async Task<int> ConnectAsync(Settings settings, bool interactive)
        {
            var errors = settings.Validate();
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    Console.Error.WriteLine("Error: " + error);
                }

                return 1;
            }

            if (!WindowsNetwork.IsElevated)
            {
                Console.Error.WriteLine("This tool must run as administrator to create the tunnel adapter.");
                Console.Error.WriteLine("Right-click OpenSstpWin.exe and choose \"Run as administrator\".");
                return 4;
            }

            if (string.IsNullOrEmpty(settings.Password) && interactive)
            {
                Console.Write("Password: ");
                settings.Password = ReadPassword();
                Console.WriteLine();
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseLog();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                _stopRequested = true;
                _session?.Stop();
            };

            _stopRequested = false;
            var attempt = 0;
            var maxAttempts = Math.Max(0, settings.ReconnectionCount);

            while (true)
            {
                OpenLog(settings);
                var lastState = TunnelState.Idle;
                var session = new VpnSession(
                    settings,
                    progress =>
                    {
                        lastState = progress.State;
                        WriteLine(FormatProgress(progress));
                    },
                    WriteLine);

                _session = session;
                var result = await session.RunAsync(CancellationToken.None).ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                _session = null;
                CloseLog();

                WriteLine(string.Empty);
                var failed = lastState == TunnelState.Failed;
                WriteLine(failed ? "FAILED: " + result.Message : "Disconnected.");

                if (!settings.EnableReconnection || _stopRequested || attempt >= maxAttempts)
                {
                    return failed ? 3 : 0;
                }

                attempt++;
                var seconds = Math.Max(1, settings.ReconnectionIntervalSeconds);
                WriteLine($"Reconnecting in {seconds}s (attempt {attempt}/{maxAttempts})…");

                var cancelled = false;
                for (var remaining = seconds; remaining > 0; remaining--)
                {
                    if (_stopRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    await Task.Delay(1000).ConfigureAwait(false);
                }

                if (cancelled)
                {
                    return 0;
                }
            }
        }

        private static int ListProfiles()
        {
            var store = ProfileStore.CreateDefault();
            if (!string.IsNullOrEmpty(store.LoadError))
            {
                Console.Error.WriteLine("Warning: the old profiles file could not be read: " + store.LoadError);
            }

            if (store.Profiles.Count == 0)
            {
                Console.WriteLine("No saved configurations. Use --save-profile <name> to add one.");
                return 0;
            }

            Console.WriteLine("Saved configurations (" + Settings.ProfilesPath + "):");
            foreach (var profile in store.All)
            {
                var endpoint = string.IsNullOrWhiteSpace(profile.Settings.Host)
                    ? "(no server)"
                    : profile.Settings.Host + ":" + profile.Settings.Port;
                Console.WriteLine($"  {profile.Name}\t{endpoint}\t{profile.Settings.Username}");
            }

            return 0;
        }

        private static int SaveProfile(Settings settings, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("A configuration name is required.");
                return 1;
            }

            var store = ProfileStore.CreateDefault();
            var replaced = store.Find(name) is not null;
            store.AddOrUpdate(name, settings.Clone());
            Console.WriteLine((replaced ? "Replaced" : "Saved") + " configuration \"" + name + "\" in " +
                              Settings.ProfilesPath);
            return 0;
        }

        private static int ImportProfile(Settings settings, string path)
        {
            try
            {
                settings.CopyFrom(SettingsFile.Import(path));
                settings.Save();
                Console.WriteLine("Profile imported into " + SettingsPath);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Could not import the profile: " + exception.Message);
                return 1;
            }
        }

        private static int ExportProfile(Settings settings, string path)
        {
            try
            {
                SettingsFile.Export(settings, path);
                Console.WriteLine("Profile (including the plaintext password) exported to " + path);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Could not export the profile: " + exception.Message);
                return 1;
            }
        }

        private static int ImportEndpoints(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > SourceFinder.MaxDownloadBytes)
                {
                    Console.Error.WriteLine("The endpoint list is larger than 1 MiB.");
                    return 1;
                }

                var text = Encoding.UTF8.GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
                var endpoints = EndpointParser.ParseText(text, 200, out var exceeded);
                if (exceeded)
                {
                    Console.Error.WriteLine("The list exceeds 200 unique endpoints; split the file first.");
                    return 1;
                }

                if (endpoints.Count == 0)
                {
                    Console.Error.WriteLine("No valid endpoints were found.");
                    return 1;
                }

                var store = ProfileStore.CreateDefault();
                var added = 0;
                foreach (var endpoint in endpoints)
                {
                    if (store.Find(endpoint.DisplayName) is not null)
                    {
                        continue;
                    }

                    if (store.AddOrUpdate(endpoint.DisplayName,
                            new Settings { Host = endpoint.Host, Port = endpoint.Port },
                            save: false))
                    {
                        added++;
                    }
                }

                store.Save();
                Console.WriteLine($"Imported {added} configuration(s) into {Settings.ProfilesPath}.");
                Console.WriteLine("Imported entries use default settings and contain no credentials.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Could not import the endpoint list: " + exception.Message);
                return 1;
            }
        }

        private static async Task<int> FindFastestAsync(Settings settings, List<string> customSources)
        {
            var sources = customSources.Count > 0
                ? customSources
                : settings.SourceUrls.Count > 0
                    ? settings.SourceUrls
                    : Settings.DefaultSourceUrls.ToList();

            var invalid = sources.Where(source => !SourceFinder.IsValidSourceUrl(source)).ToList();
            if (invalid.Count > 0)
            {
                Console.Error.WriteLine("Only HTTPS source URLs without credentials are accepted:");
                foreach (var source in invalid)
                {
                    Console.Error.WriteLine("  " + source);
                }

                return 1;
            }

            var finder = new SourceFinder();
            try
            {
                var outcome = await finder.CompareAsync(sources, null, CancellationToken.None)
                    .ConfigureAwait(false);

                Console.WriteLine($"Tested {outcome.TotalEndpoints} endpoint(s); {outcome.Results.Count} reachable.");
                var rank = 0;
                foreach (var result in outcome.Results)
                {
                    rank++;
                    Console.WriteLine($"{rank,4}. {result.Endpoint.DisplayName}  {result.LatencyMs} ms");
                }

                if (outcome.FailedSources.Count > 0)
                {
                    Console.WriteLine($"{outcome.FailedSources.Count} source(s) failed:");
                    foreach (var source in outcome.FailedSources)
                    {
                        Console.WriteLine("  " + source);
                    }
                }

                Console.WriteLine("TCP connect time only; it does not prove SSTP or the credentials work.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Comparison failed: " + exception.Message);
                return 3;
            }
        }

        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.11.0";

        public static string SettingsPath => Settings.FilePath;

        private static int ParseInt(string value) => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        private static string[] SplitList(string value) => value.Split(
            new[] { ',', ';', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static bool ParseBool(string value) => value.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new ArgumentException("expected on/off, got '" + value + "'"),
        };

        private static string FormatProgress(TunnelProgress progress) => progress.State switch
        {
            TunnelState.Connected => "[CONNECTED] " + progress.Message,
            TunnelState.Failed => "[ERROR] " + progress.Message,
            TunnelState.Disconnected => "[STOPPED] " + progress.Message,
            _ => "  " + progress.Message,
        };

        private static void ApplySetting(Settings settings, string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "host": settings.Host = value; break;
                case "port": settings.Port = ParseInt(value); break;
                case "username" or "user": settings.Username = value; break;
                case "password": settings.Password = value; break;
                case "auth": settings.AuthProtocols = value.Split(',').ToList(); break;
                case "mtu": settings.Mtu = ParseInt(value); break;
                case "mru": settings.Mru = ParseInt(value); break;
                case "authtimeout": settings.AuthTimeoutSeconds = ParseInt(value); break;
                case "adapter": settings.AdapterName = value; break;
                case "verify": settings.VerifyServerCertificate = ParseBool(value); break;
                case "defaultroute": settings.AddDefaultRoute = ParseBool(value); break;
                case "privateroutes": settings.RoutePrivateNetworks = ParseBool(value); break;
                case "customroutes": settings.AddCustomRoutes = ParseBool(value); break;
                case "ipv4": settings.EnableIpv4 = ParseBool(value); break;
                case "ipv6": settings.EnableIpv6 = ParseBool(value); break;
                case "dns": settings.RequestDns = ParseBool(value); break;
                case "customdns":
                    settings.UseCustomDns = true;
                    settings.CustomDnsAddress = value;
                    break;
                case "staticip":
                    settings.RequestStaticIpv4 = true;
                    settings.StaticIpv4Address = value;
                    break;
                case "tls": settings.TlsVersion = TlsModes.Normalize(value); break;
                case "sni":
                    settings.UseCustomSni = true;
                    settings.CustomSniHostname = value;
                    break;
                case "ca":
                    settings.UseCustomCa = true;
                    settings.CaBundlePath = value;
                    break;
                case "ciphersuites":
                    settings.UseCipherSuites = true;
                    settings.CipherSuites = SplitList(value).ToList();
                    break;
                case "useproxy": settings.UseProxy = ParseBool(value); break;
                case "proxyhost": settings.ProxyHost = value; break;
                case "proxyport": settings.ProxyPort = ParseInt(value); break;
                case "proxyuser": settings.ProxyUsername = value; break;
                case "proxypassword": settings.ProxyPassword = value; break;
                case "reconnect":
                    var count = ParseInt(value);
                    settings.EnableReconnection = count > 0;
                    settings.ReconnectionCount = count;
                    break;
                case "reconnectinterval": settings.ReconnectionIntervalSeconds = ParseInt(value); break;
                case "logdir": settings.LogFolder = value; break;
                case "savelog": settings.SaveLog = ParseBool(value); break;
                case "theme": settings.AppTheme = AppThemes.Normalize(value); break;
                default:
                    throw new ArgumentException("Unknown setting: " + key);
            }
        }

        private static void PrintSettings(Settings settings)
        {
            Console.WriteLine("Host:            " + settings.Host);
            Console.WriteLine("Port:            " + settings.Port);
            Console.WriteLine("Username:        " + settings.Username);
            Console.WriteLine("Password:        " + (string.IsNullOrEmpty(settings.Password) ? "(not set)" : "********"));
            Console.WriteLine("Auth protocols:  " + string.Join(", ", settings.AuthProtocols));
            Console.WriteLine("TLS version:     " + settings.TlsVersion);
            Console.WriteLine("Verify cert:     " + settings.VerifyServerCertificate);
            Console.WriteLine("Custom CA:       " +
                              (settings.UseCustomCa ? settings.CaBundlePath : "(system store)"));
            Console.WriteLine("Custom SNI:      " +
                              (settings.UseCustomSni ? settings.CustomSniHostname : "(endpoint host)"));
            Console.WriteLine("MTU / MRU:       " + settings.Mtu + " / " + settings.Mru);
            Console.WriteLine("IPv4 / IPv6:     " + settings.EnableIpv4 + " / " + settings.EnableIpv6);
            Console.WriteLine("Static IPv4:     " + (settings.HasStaticIpv4 ? settings.StaticIpv4Address : "(no)"));
            Console.WriteLine("DNS:             " +
                              (settings.UseCustomDns
                                  ? settings.CustomDnsAddress
                                  : settings.RequestDns ? "(from server)" : "(unchanged)"));
            Console.WriteLine("Default route:   " + settings.AddDefaultRoute);
            Console.WriteLine("Private routes:  " + settings.RoutePrivateNetworks);
            Console.WriteLine("Custom routes:   " + string.Join(", ", settings.CustomRoutes));
            Console.WriteLine("Adapter name:    " + settings.AdapterName);
            Console.WriteLine("Reconnection:    " +
                              (settings.EnableReconnection
                                  ? settings.ReconnectionCount + " attempt(s), " +
                                    settings.ReconnectionIntervalSeconds + "s apart"
                                  : "off"));
            Console.WriteLine("Theme:           " + settings.AppTheme);
            Console.WriteLine("Log folder:      " + settings.LogDirectory);
            Console.WriteLine("Source lists:    " + settings.SourceUrls.Count);
            Console.WriteLine("Settings file:   " + SettingsPath);
            Console.WriteLine("Profiles file:   " + Settings.ProfilesPath);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Open SSTP Client for Windows " + Version + " (win-x64, portable)");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  OpenSstpWin.exe --host <server> --user <name> --password <secret> [options]");
            Console.WriteLine("  OpenSstpWin.exe --list-profiles | --profile <name> | --save-profile <name>");
            Console.WriteLine("  OpenSstpWin.exe --find-fastest [--source <https-url> …]");
            Console.WriteLine();
            Console.WriteLine("Connection:");
            Console.WriteLine("  --port <n>              SSTP port (default 443)");
            Console.WriteLine("  --auth <list>           Comma separated: PAP,MSCHAPv2,EAP-MSCHAPv2");
            Console.WriteLine("  --auth-timeout <s>      PPP authentication timeout (default 15)");
            Console.WriteLine("  --tls <mode>            DEFAULT, TLS12 or TLS13");
            Console.WriteLine("  --insecure              Do not verify the server certificate chain");
            Console.WriteLine("  --ca <file-or-dir>      Trust only these certificates (PEM/CRT/CER)");
            Console.WriteLine("  --sni <hostname>        Send a custom TLS server name");
            Console.WriteLine("  --cipher-suites <list>  Best-effort cipher-suite restriction");
            Console.WriteLine("  --mtu <n> / --mru <n>   Tunnel MTU/MRU (default 1500)");
            Console.WriteLine("  --ipv4 on|off           Enable IPCP (default on)");
            Console.WriteLine("  --ipv6 on|off           Enable IPv6CP (default off)");
            Console.WriteLine("  --static-ip <address>   Ask the server for a specific IPv4 address");
            Console.WriteLine("  --default-route         Route all traffic through the VPN (default)");
            Console.WriteLine("  --no-default-route      Only the private/custom routes are tunneled");
            Console.WriteLine("  --route <prefix>        Extra prefix to route into the tunnel (repeatable)");
            Console.WriteLine("  --private-routes on|off Route RFC1918 networks through the VPN");
            Console.WriteLine("  --custom-routes on|off  Add the --route prefixes");
            Console.WriteLine("  --dns on|off            Request DNS servers from the server");
            Console.WriteLine("  --custom-dns <list>     Use these IPv4 DNS servers instead");
            Console.WriteLine("  --proxy <host:port>     Connect through an HTTP CONNECT proxy");
            Console.WriteLine("  --proxy-user <name>     Proxy credentials");
            Console.WriteLine("  --proxy-password <pw>   Proxy credentials");
            Console.WriteLine("  --reconnect <n>         Retry a failed/dropped connection n times");
            Console.WriteLine("  --reconnect-interval <s> Seconds between retries (default 10)");
            Console.WriteLine("  --adapter <name>        Wintun adapter name (default OpenSSTP)");
            Console.WriteLine("  --log-dir <folder>      Where session logs are written");
            Console.WriteLine("  --no-log                Do not write a session log");
            Console.WriteLine("  --theme <mode>          System, Light or Dark");
            Console.WriteLine("  --interactive, -i       Prompt for the password");
            Console.WriteLine();
            Console.WriteLine("Configurations:");
            Console.WriteLine("  --list-profiles         List saved configurations");
            Console.WriteLine("  --profile <name>        Load a saved configuration for this run");
            Console.WriteLine("  --save-profile <name>   Save the current settings as a configuration");
            Console.WriteLine("  --import-profile <file> Import a settings JSON profile");
            Console.WriteLine("  --export-profile <file> Export the current settings (plaintext password)");
            Console.WriteLine("  --import-endpoints <file> Import an endpoint list (max 200 entries)");
            Console.WriteLine("  --find-fastest          Compare the configured HTTPS sources over TCP");
            Console.WriteLine("  --source <https-url>    Source list for --find-fastest (repeatable)");
            Console.WriteLine();
            Console.WriteLine("Other:");
            Console.WriteLine("  --set <key> <value>     Persist a setting for future runs");
            Console.WriteLine("  --show                  Print the stored settings");
            Console.WriteLine("  --version, --help");
            Console.WriteLine();
            Console.WriteLine("Press Ctrl+C to disconnect. Settings: " + SettingsPath);
        }

        private static string ReadPassword()
        {
            var buffer = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                    }

                    continue;
                }

                buffer.Append(key.KeyChar);
                Console.Write('*');
            }

            return buffer.ToString();
        }

        private static void OpenLog(Settings settings)
        {
            if (!settings.SaveLog)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(settings.LogDirectory);
                var path = Path.Combine(settings.LogDirectory,
                    $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _logWriter = new StreamWriter(path, append: true) { AutoFlush = true };
            }
            catch
            {
                _logWriter = null;
            }
        }

        private static void CloseLog()
        {
            lock (LogLock)
            {
                try
                {
                    _logWriter?.Dispose();
                }
                catch
                {
                    // A failed flush must not abort shutdown.
                }

                _logWriter = null;
            }
        }

        internal static void WriteLine(string message)
        {
            lock (LogLock)
            {
                if (_logWriter is not null)
                {
                    try
                    {
                        _logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
                    }
                    catch
                    {
                        // Logging must never break the tunnel.
                    }
                }
            }

            Console.WriteLine(message);
        }
    }
}
