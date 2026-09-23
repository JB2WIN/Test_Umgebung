using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lernheft.Studio.App;

/// <summary>
/// Die Windows-Firewall fragt beim ersten Start, ob Lernheft Studio Verbindungen annehmen darf.
/// Wer das verpasst hat, kann die Freigabe hier nachholen (braucht einmal Administratorrechte).
/// Ohne Freigabe klappt es trotzdem: Dann verbindet sich das Surface von sich aus mit dem iPad.
/// </summary>
public static class Firewall
{
    private const string RuleName = "Lernheft Studio (iPad)";

    public static bool RuleExists()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            });
            if (process is null) return false;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Legt die Freigabe an – Windows fragt dabei nach Administratorrechten.</summary>
    public static bool AddRule()
    {
        var exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return false;
        try
        {
            var arguments = $"/c netsh advfirewall firewall delete rule name=\"{RuleName}\" & " +
                            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
                            $"program=\"{exe}\" enable=yes profile=private,domain";
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            process?.WaitForExit(20000);
            return RuleExists();
        }
        catch (Exception)
        {
            // Abgelehnt oder keine Rechte.
            return false;
        }
    }
}

/// <summary>
/// Meldet das Surface im WLAN als „_lernheft._tcp“ an (Bonjour/mDNS). So findet das iPad es auch,
/// wenn man nur den sechsstelligen Code eintippt. Klappt das nicht, sucht das iPad das Netz ab.
/// </summary>
public sealed class BonjourAdvertiser : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr RegisterCompletionCallback;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        [MarshalAs(UnmanagedType.Bool)] public bool UnicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceCancel
    {
        public IntPtr Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertiesCount, string[]? keys, string[]? values);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    // Im nicht verschobenen Speicher, weil Windows die Adressen bis zum Abmelden behält.
    private IntPtr _request;
    private IntPtr _cancel;
    private RegisterComplete? _callback;
    private bool _registered;

    public bool Start(string name, int port)
    {
        try
        {
            var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or ' ').ToArray()).Trim();
            if (safe.Length == 0) safe = "Surface";
            var host = Environment.MachineName + ".local";
            var instance = DnsServiceConstructInstance($"{safe}._lernheft._tcp.local", host, IntPtr.Zero, IntPtr.Zero,
                (ushort)port, 0, 0, 1, new[] { "v" }, new[] { PadProtocol.Version.ToString() });
            if (instance == IntPtr.Zero) return false;
            _callback = (_, _, registered) =>
            {
                if (registered != IntPtr.Zero) DnsServiceFreeInstance(registered);
            };
            var request = new DnsServiceRegisterRequest
            {
                Version = 1,
                InterfaceIndex = 0,
                ServiceInstance = instance,
                RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_callback),
                QueryContext = IntPtr.Zero,
                Credentials = IntPtr.Zero,
                UnicastEnabled = false
            };
            _request = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceRegisterRequest>());
            Marshal.StructureToPtr(request, _request, false);
            _cancel = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceCancel>());
            Marshal.StructureToPtr(new DnsServiceCancel(), _cancel, false);
            var status = DnsServiceRegister(_request, _cancel);
            _registered = status == 9701; // DNS_REQUEST_PENDING
            return _registered;
        }
        catch (Exception error)
        {
            Services.Log("Bonjour: " + error.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (!_registered) return;
        try
        {
            DnsServiceDeRegister(_request, IntPtr.Zero);
        }
        catch (Exception) { }
        _registered = false;
    }
}
