using System.Reflection;
using OpenSstpWin;
using OpenSstpWin.Core;
using OpenSstpWin.Interop;

if (args.Length == 0)
{
    DesktopApp.Run();
    return 0;
}

return await App.RunAsync(args).ConfigureAwait(false);

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
                    case "--set" when i + 2 < args.Length:
                        ApplySetting(settings, args[i + 1], args[i + 2]);
                        i += 2;
                        settings.Save();
                        Console.WriteLine($"{args[i - 1]} saved to {SettingsPath}");
                        return 0;
                    case "--show":
                        PrintSettings(settings);
                        return 0;
                    case "--interactive" or "-i":
                        interactive = true;
                        break;
                    case "--host" when i + 1 < args.Length:
                        settings.Host = args[++i];
                        break;
                    case "--port" when i + 1 < args.Length:
                        settings.Port = int.Parse(args[++i]);
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
                    case "--insecure":
                        settings.VerifyServerCertificate = false;
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
                    case "--adapter" when i + 1 < args.Length:
                        settings.AdapterName = args[++i];
                        break;
                    case "--ca" when i + 1 < args.Length:
                        settings.CaBundlePath = args[++i];
                        break;
                    case "--dns" when i + 1 < args.Length:
                        settings.RequestDns = args[++i] != "off";
                        break;
                    case "--mtu" when i + 1 < args.Length:
                        settings.Mtu = int.Parse(args[++i]);
                        break;
                    case "--proxy" when i + 1 < args.Length:
                    {
                        var parts = args[++i].Split(':');
                        settings.UseProxy = true;
                        settings.ProxyHost = parts[0];
                        settings.ProxyPort = parts.Length > 1 ? int.Parse(parts[1]) : 8080;
                        break;
                    }

                    case "--proxy-user" when i + 1 < args.Length:
                        settings.ProxyUsername = args[++i];
                        break;
                    case "--proxy-password" when i + 1 < args.Length:
                        settings.ProxyPassword = args[++i];
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

            if (mode == "disconnect")
            {
                Console.WriteLine("No background VPN service is running. Press Ctrl+C in the window that is connected.");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(settings.Host))
            {
                Console.Error.WriteLine("A host is required. Example: OpenSstpWin.exe --host vpn.example.com --user me --password secret");
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
                Session?.Stop();
            };

            if (settings.SaveLog)
            {
                OpenLog(settings);
            }

            var lastState = TunnelState.Idle;
            var session = new VpnSession(
                settings,
                progress =>
                {
                    lastState = progress.State;
                    WriteLine(FormatProgress(progress));
                },
                WriteLine);

            Session = session;

            using var stopOnCancel = new CancellationTokenSource();
            var result = await session.RunAsync(stopOnCancel.Token).ConfigureAwait(false);

            WriteLine(string.Empty);
            WriteLine(lastState == TunnelState.Failed ? "FAILED: " + result.Message : "Disconnected.");
            CloseLog();

            return lastState == TunnelState.Failed ? 3 : 0;
        }

        private static VpnSession? Session;

        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.10.5";

        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSstpWin",
            "settings.json");

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
                case "port": settings.Port = int.Parse(value); break;
                case "username" or "user": settings.Username = value; break;
                case "password": settings.Password = value; break;
                case "auth": settings.AuthProtocols = value.Split(',').ToList(); break;
                case "mtu": settings.Mtu = int.Parse(value); break;
                case "mru": settings.Mru = int.Parse(value); break;
                case "adapter": settings.AdapterName = value; break;
                case "verify": settings.VerifyServerCertificate = value is "1" or "true" or "yes"; break;
                case "defaultroute": settings.AddDefaultRoute = value is "1" or "true" or "yes"; break;
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
            Console.WriteLine("Verify cert:     " + settings.VerifyServerCertificate);
            Console.WriteLine("MTU / MRU:       " + settings.Mtu + " / " + settings.Mru);
            Console.WriteLine("Default route:   " + settings.AddDefaultRoute);
            Console.WriteLine("Private routes:  " + settings.RoutePrivateNetworks);
            Console.WriteLine("Adapter name:    " + settings.AdapterName);
            Console.WriteLine("Settings file:   " + SettingsPath);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Open SSTP Client for Windows " + Version + " (win-x64, portable)");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  OpenSstpWin.exe --host <server> --user <name> --password <secret> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --port <n>              SSTP port (default 443)");
            Console.WriteLine("  --auth <list>           Comma separated: PAP,MSCHAPv2,EAP-MSCHAPv2");
            Console.WriteLine("  --insecure              Do not verify the server certificate chain");
            Console.WriteLine("  --ca <file.pem>         Extra trusted root certificates");
            Console.WriteLine("  --mtu <n>               Interface MTU (default 1500)");
            Console.WriteLine("  --default-route         Route all traffic through the VPN (default)");
            Console.WriteLine("  --no-default-route      Only the private/custom routes are tunneled");
            Console.WriteLine("  --route <prefix>        Extra prefix to route into the tunnel (repeatable)");
            Console.WriteLine("  --dns off               Do not apply the DNS servers from the server");
            Console.WriteLine("  --proxy <host:port>     Connect through an HTTP CONNECT proxy");
            Console.WriteLine("  --proxy-user <name>     Proxy credentials");
            Console.WriteLine("  --proxy-password <pw>   Proxy credentials");
            Console.WriteLine("  --adapter <name>        Wintun adapter name (default OpenSSTP)");
            Console.WriteLine("  --interactive, -i       Prompt for the password");
            Console.WriteLine("  --set <key> <value>     Persist a setting for future runs");
            Console.WriteLine("  --show                  Print the stored settings");
            Console.WriteLine("  --version, --help");
            Console.WriteLine();
            Console.WriteLine("Press Ctrl+C to disconnect. Settings: " + SettingsPath);
        }

        private static string ReadPassword()
        {
            var buffer = new System.Text.StringBuilder();
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
            try
            {
                Directory.CreateDirectory(settings.LogDirectory);
                var path = Path.Combine(settings.LogDirectory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
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
                _logWriter?.Dispose();
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

            if (!Console.IsOutputRedirected)
            {
                Console.WriteLine(message);
            }
            else
            {
                Console.WriteLine(message);
            }
        }
    }
}
