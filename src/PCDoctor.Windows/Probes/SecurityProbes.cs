using Microsoft.Win32;
using PCDoctor.Core;

namespace PCDoctor.Windows.Probes;

/// <summary>
/// Antivirus registrados en Windows, Microsoft Defender, firewall, programas de acceso remoto instalados
/// (señal típica de estafas de "soporte técnico") y archivo hosts.
/// </summary>
internal sealed class SecurityProbe : ProbeBase
{
    internal static readonly string[] RemoteAccessTools =
    [
        "AnyDesk", "TeamViewer", "RustDesk", "UltraViewer", "ScreenConnect", "ConnectWise Control", "Splashtop",
        "Supremo", "LogMeIn", "AeroAdmin", "Ammyy", "Chrome Remote Desktop", "DWService", "RemotePC", "Zoho Assist", "GoTo Resolve",
    ];

    private const string FirewallPolicy = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    public override string Id => "seguridad";
    public override string Title => "Protección contra virus y ataques";
    public override IReadOnlyList<string> Areas => [Core.Areas.Security];
    public override IReadOnlyList<string> FactKeys =>
    [
        "seguridad.antivirus_activo", "seguridad.antivirus_nombres", "seguridad.antivirus_actualizado", "seguridad.defender_activo",
        "seguridad.defender_firmas_dias", "seguridad.firewall_windows_activo", "seguridad.firewall_terceros_activo",
        "seguridad.acceso_remoto", "seguridad.acceso_remoto_cantidad", "seguridad.hosts_entradas", "seguridad.hosts_detalle",
    ];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        Part(r, "Antivirus registrados", () =>
        {
            var avs = Wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            var active = new List<string>();
            var upToDate = true;
            foreach (var av in avs)
            {
                var (on, current) = DecodeProductState(av.Get<uint>("productState"));
                var name = av.Str("displayName");
                r.Detail($"Antivirus: {name}", $"{(on ? "activo" : "desactivado")}{(on ? current ? ", actualizado" : ", DESACTUALIZADO" : "")}");
                if (on) { active.Add(name); upToDate &= current; }
            }
            r.Facts.Set("seguridad.antivirus_activo", active.Count > 0);
            if (active.Count > 0)
            {
                r.Facts.Set("seguridad.antivirus_nombres", string.Join(", ", active));
                r.Facts.Set("seguridad.antivirus_actualizado", upToDate);
            }
            if (avs.Count == 0) r.Detail("Antivirus", "ninguno registrado");

            var fws = Wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM FirewallProduct");
            r.Facts.Set("seguridad.firewall_terceros_activo", fws.Any(f => DecodeProductState(f.Get<uint>("productState")).enabled));
        });

        Part(r, "Microsoft Defender", () =>
        {
            var rows = Wmi.Query(@"root\Microsoft\Windows\Defender", "SELECT RealTimeProtectionEnabled, AntivirusSignatureAge, AMRunningMode FROM MSFT_MpComputerStatus");
            if (rows.Count == 0) throw new InvalidOperationException("Defender no informa estado");
            var s = rows[0];
            var mode = s.Str("AMRunningMode");
            var realTime = s.Get<bool>("RealTimeProtectionEnabled");
            var active = realTime && (mode.Length == 0 || mode.Equals("Normal", StringComparison.OrdinalIgnoreCase));
            r.Facts.Set("seguridad.defender_activo", active);
            r.Detail("Microsoft Defender", active ? "protección en tiempo real activa" : $"no es el antivirus principal ({(mode.Length > 0 ? mode : "tiempo real desactivado")})");
            if (active)
            {
                var age = s.Get<uint>("AntivirusSignatureAge");
                r.Facts.Set("seguridad.defender_firmas_dias", age);
                r.Detail("Antigüedad de las firmas de Defender", $"{age} días");
            }
        });

        Part(r, "Firewall de Windows", () =>
        {
            // El perfil de dominio solo cuenta si la PC está en un dominio; las políticas mandan sobre la configuración local.
            var inDomain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName.Length > 0;
            var profiles = new[] { ("DomainProfile", "dominio"), ("StandardProfile", "privada"), ("PublicProfile", "pública") }
                .Where(p => inDomain || p.Item1 != "DomainProfile");
            var allOn = true;
            foreach (var (key, label) in profiles)
            {
                var v = Reg.Int(RegistryHive.LocalMachine, $@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{key}", "EnableFirewall")
                        ?? Reg.Int(RegistryHive.LocalMachine, $@"{FirewallPolicy}\{key}", "EnableFirewall");
                if (v is null) throw new InvalidOperationException("configuración no disponible");
                allOn &= v == 1;
                r.Detail($"Firewall (red {label})", v == 1 ? "activado" : "DESACTIVADO");
            }
            r.Facts.Set("seguridad.firewall_windows_activo", allOn);
        });

        Part(r, "Programas de acceso remoto", () =>
        {
            var found = InstalledPrograms()
                .Where(p => RemoteAccessTools.Any(t => p.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            r.Facts.Set("seguridad.acceso_remoto_cantidad", found.Count);
            if (found.Count > 0) r.Facts.Set("seguridad.acceso_remoto", string.Join(", ", found));
            r.Detail("Programas de acceso remoto instalados", found.Count == 0 ? "ninguno" : string.Join(", ", found));
        });

        Part(r, "Archivo hosts", () =>
        {
            var path = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
            var entries = File.ReadLines(path)
                .Select(l => l.Split('#')[0].Trim())
                .Where(l => l.Length > 0)
                .Where(l => !IsLocalhostLine(l))
                .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+", " → "))
                .ToList();
            r.Facts.Set("seguridad.hosts_entradas", entries.Count);
            if (entries.Count > 0) r.Facts.Set("seguridad.hosts_detalle", string.Join("; ", entries.Take(5)) + (entries.Count > 5 ? $" y {entries.Count - 5} más" : ""));
            r.Detail("Redirecciones en el archivo hosts", entries.Count);
        });
        return Task.CompletedTask;
    }

    private static bool IsLocalhostLine(string line)
    {
        var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts.Skip(1).All(h => h.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>productState de Security Center: byte medio 0x10/0x11 = activo; byte bajo 0x00 = firmas al día.</summary>
    internal static (bool enabled, bool upToDate) DecodeProductState(uint state)
    {
        var mid = (state >> 8) & 0xFF;
        var low = state & 0xFF;
        return (mid is 0x10 or 0x11, low == 0x00);
    }

    internal static IEnumerable<string> InstalledPrograms()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(uninstall);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                string? name;
                try
                {
                    using var k = key.OpenSubKey(sub);
                    name = k?.GetValue("DisplayName") as string;
                }
                catch (System.Security.SecurityException) { continue; }
                if (!string.IsNullOrWhiteSpace(name)) yield return name.Trim();
            }
        }
    }
}
