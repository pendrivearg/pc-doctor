using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PCDoctor.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PCDoctor.Windows.Probes;

internal sealed class LifecycleData
{
    public List<string> Fuentes { get; set; } = [];
    public string NotaWindows10 { get; set; } = "";
    public List<LifecycleEntry> Versiones { get; set; } = [];

    public static LifecycleData Load(string dataDir) =>
        new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
            .Deserialize<LifecycleData>(File.ReadAllText(Path.Combine(dataDir, "windows-lifecycle.yaml")));
}

internal sealed class LifecycleEntry
{
    public int Build { get; set; }
    public string Nombre { get; set; } = "";
    [YamlMember(Alias = "consumo")] public string ConsumoText { get; set; } = "";
    [YamlMember(Alias = "empresa")] public string EmpresaText { get; set; } = "";
    [YamlMember(Alias = "ltsc")] public string? LtscText { get; set; }
    [YamlMember(Alias = "ltsc_iot")] public string? LtscIotText { get; set; }
    public DateTime? LtscIot => LtscIotText is null ? null : ParseDate(LtscIotText);

    public DateTime Consumo => ParseDate(ConsumoText);
    public DateTime Empresa => ParseDate(EmpresaText);
    public DateTime? Ltsc => LtscText is null ? null : ParseDate(LtscText);

    private static DateTime ParseDate(string s) => DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Versión de Windows, fin de soporte, reinicios pendientes y tiempo encendida.</summary>
internal sealed class SystemProbe : ProbeBase
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public override string Id => "sistema";
    public override string Title => "Windows";
    public override IReadOnlyList<string> Areas => [Core.Areas.Windows, Core.Areas.Performance];
    public override IReadOnlyList<string> FactKeys =>
    [
        "sistema.nombre", "sistema.windows", "sistema.build", "sistema.version", "sistema.edicion", "sistema.arquitectura",
        "sistema.insider", "sistema.fin_soporte", "sistema.fuera_de_soporte", "sistema.dias_para_fin_soporte",
        "sistema.horas_encendida", "sistema.reinicio_pendiente", "sistema.es_admin", "sistema.nota_soporte",
    ];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        var lm = RegistryHive.LocalMachine;
        var build = int.TryParse(Reg.Str(lm, CurrentVersion, "CurrentBuildNumber"), out var b) ? b : Environment.OSVersion.Version.Build;
        var ubr = Reg.Int(lm, CurrentVersion, "UBR");
        var edition = Reg.Str(lm, CurrentVersion, "EditionID") ?? "";
        var display = Reg.Str(lm, CurrentVersion, "DisplayVersion") ?? Reg.Str(lm, CurrentVersion, "ReleaseId") ?? "";
        var major = build >= 22000 ? 11 : 10; // el registro sigue diciendo "Windows 10" en ProductName de Windows 11
        var name = $"Windows {major} {EditionName(edition)}".Trim();

        r.Facts.Set("sistema.nombre", name);
        r.Facts.Set("sistema.windows", major);
        r.Facts.Set("sistema.build", build);
        r.Facts.Set("sistema.version", display);
        r.Facts.Set("sistema.edicion", edition);
        r.Facts.Set("sistema.arquitectura", RuntimeInformation.OSArchitecture.ToString());
        r.Facts.Set("sistema.es_admin", ctx.IsElevated);
        r.Detail("Sistema", $"{name} {display}");
        r.Detail("Compilación", ubr is null ? build.ToString() : $"{build}.{ubr}");
        r.Detail("Arquitectura", RuntimeInformation.OSArchitecture.ToString());

        Part(r, "Fin de soporte", () =>
        {
            var data = LifecycleData.Load(ctx.Paths.DataDir);
            var entry = data.Versiones.FirstOrDefault(v => v.Build == build);
            var maxKnown = data.Versiones.Max(v => v.Build);
            var isLtsc = edition.Contains("EnterpriseS", StringComparison.Ordinal); // EnterpriseS, EnterpriseSN, IoTEnterpriseS
            var isIot = edition.StartsWith("IoT", StringComparison.Ordinal);
            if (entry is null)
            {
                if (build > maxKnown)
                {
                    r.Facts.Set("sistema.insider", true);
                    r.Detail("Fin de soporte", "Versión más nueva que las publicadas (Insider o de prueba)");
                }
                else
                {
                    // Versiones viejas no listadas: todas están fuera de soporte desde hace años.
                    r.Facts.Set("sistema.insider", false);
                    r.Facts.Set("sistema.fuera_de_soporte", true);
                    r.Facts.Set("sistema.fin_soporte", "hace años");
                    r.Detail("Fin de soporte", "Versión antigua, fuera de soporte");
                }
                return;
            }
            r.Facts.Set("sistema.insider", false);
            var enterprise = edition.StartsWith("Enterprise", StringComparison.Ordinal) || edition is "Education" or "IoTEnterprise";
            DateTime end = isLtsc && isIot && entry.LtscIot is { } li ? li
                : isLtsc && entry.Ltsc is { } l ? l
                : enterprise ? entry.Empresa : entry.Consumo;
            var days = (int)Math.Floor((end.Date - DateTime.Today).TotalDays);
            r.Facts.Set("sistema.fin_soporte", end.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            r.Facts.Set("sistema.dias_para_fin_soporte", days);
            r.Facts.Set("sistema.fuera_de_soporte", days < 0);
            if (major == 10) r.Facts.Set("sistema.nota_soporte", data.NotaWindows10);
            r.Detail("Fin de soporte", $"{end:dd/MM/yyyy} ({entry.Nombre})");
        });

        var hours = Environment.TickCount64 / 3_600_000.0;
        r.Facts.Set("sistema.horas_encendida", Math.Round(hours, 1));
        r.Detail("Encendida sin reiniciar", hours >= 48 ? $"{hours / 24:0.#} días" : $"{hours:0.#} horas");

        var pending = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
                      || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        r.Facts.Set("sistema.reinicio_pendiente", pending);
        r.Detail("Reinicio pendiente por actualizaciones", pending);
        return Task.CompletedTask;
    }

    private static bool KeyExists(string path)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(path);
        return key is not null;
    }

    private static string EditionName(string edition) => edition switch
    {
        "Core" or "CoreSingleLanguage" or "CoreCountrySpecific" => "Home",
        "Professional" => "Pro",
        "ProfessionalWorkstation" => "Pro para Workstations",
        "ProfessionalEducation" => "Pro Education",
        "Enterprise" => "Enterprise",
        "EnterpriseS" => "Enterprise LTSC",
        "Education" => "Education",
        "IoTEnterprise" => "IoT Enterprise",
        "IoTEnterpriseS" => "IoT Enterprise LTSC",
        _ => edition,
    };
}

/// <summary>Estado de activación de Windows, leído por CIM (sin slmgr.vbs, que depende de VBScript).</summary>
internal sealed class LicenseProbe : ProbeBase
{
    public override string Id => "licencia";
    public override string Title => "Activación de Windows";
    public override IReadOnlyList<string> Areas => [Core.Areas.Windows];
    public override IReadOnlyList<string> FactKeys => ["licencia.activado", "licencia.estado", "licencia.estado_texto"];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        Part(r, "Estado de activación", () =>
        {
            var rows = Wmi.Cimv2("SELECT Name, LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL");
            if (rows.Count == 0)
            {
                r.Facts.Set("licencia.activado", false);
                r.Facts.Set("licencia.estado", 0);
                r.Facts.Set("licencia.estado_texto", "sin clave de producto instalada");
                r.Detail("Activación", "Sin clave de producto instalada");
                return;
            }
            var status = rows.Select(o => o.Get<uint>("LicenseStatus")).Max();
            var text = status switch
            {
                1 => "activado",
                0 => "sin licencia",
                2 => "período de gracia inicial",
                3 => "período de gracia adicional",
                4 => "período de gracia por cambio de hardware",
                5 => "notificación de licencia (sin activar)",
                6 => "período de gracia extendido",
                _ => $"estado {status}",
            };
            r.Facts.Set("licencia.activado", status == 1);
            r.Facts.Set("licencia.estado", status);
            r.Facts.Set("licencia.estado_texto", text);
            r.Detail("Activación", text);
        });
        return Task.CompletedTask;
    }
}

/// <summary>UEFI, Secure Boot, TPM y BitLocker: lo que importa antes de tocar la BIOS o arrancar desde un USB.</summary>
internal sealed class PlatformProbe : ProbeBase
{
    public override string Id => "plataforma";
    public override string Title => "Arranque y seguridad del equipo (UEFI, Secure Boot, TPM, BitLocker)";
    public override IReadOnlyList<string> Areas => [Core.Areas.Windows, Core.Areas.Security, Core.Areas.Hardware];
    public override IReadOnlyList<string> FactKeys =>
        ["plataforma.uefi", "plataforma.secure_boot", "plataforma.tpm_presente", "plataforma.tpm_version", "plataforma.bitlocker_sistema"];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        Part(r, "Tipo de firmware", () =>
        {
            if (!Native.GetFirmwareType(out var fw)) throw new InvalidOperationException("GetFirmwareType falló");
            r.Facts.Set("plataforma.uefi", fw == 2);
            r.Detail("Firmware", fw == 2 ? "UEFI" : fw == 1 ? "BIOS heredada (Legacy)" : "desconocido");
        });
        Part(r, "Secure Boot", () =>
        {
            var v = Reg.Int(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            if (v is null) throw new InvalidOperationException("no informado por el equipo");
            r.Facts.Set("plataforma.secure_boot", v == 1);
            r.Detail("Secure Boot", v == 1 ? "activado" : "desactivado");
        });
        Part(r, "Chip de seguridad TPM", () =>
        {
            var rows = Wmi.Query(@"root\cimv2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm");
            r.Facts.Set("plataforma.tpm_presente", rows.Count > 0);
            if (rows.Count == 0) { r.Detail("TPM", "no detectado"); return; }
            var spec = rows[0].Str("SpecVersion").Split(',')[0].Trim();
            if (double.TryParse(spec, NumberStyles.Float, CultureInfo.InvariantCulture, out var ver)) r.Facts.Set("plataforma.tpm_version", ver);
            r.Detail("TPM", $"versión {spec}");
        });
        Part(r, "Cifrado BitLocker del disco del sistema", () =>
        {
            var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
            var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell no disponible");
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                object? value = shell.NameSpace(drive + "\\").Self.ExtendedProperty("System.Volume.BitLockerProtection");
                var state = value is null ? -1 : Convert.ToInt32(value);
                // 1/3/5/6 = cifrado presente (activo, cifrando, suspendido, bloqueado); 2 = desactivado.
                bool? on = state switch { 1 or 3 or 5 or 6 => true, 2 => false, _ => null };
                if (on is null) throw new InvalidOperationException("estado no informado");
                r.Facts.Set("plataforma.bitlocker_sistema", on.Value);
                r.Detail($"BitLocker en {drive}", on.Value ? "activado" : "desactivado");
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        });
        return Task.CompletedTask;
    }
}
