using System.Reflection;
using PCDoctor.Core;
using PCDoctor.Core.Diagnostics;
using PCDoctor.Core.Knowledge;
using PCDoctor.Windows;

namespace PCDoctor.Tests;

internal static class Repo
{
    public static string Root { get; } = Path.GetFullPath(
        typeof(Repo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!);

    public static KnowledgeBase Kb { get; } = KnowledgeBase.Load(Path.Combine(Root, "knowledge", "problems"));
}

public class KnowledgeTests
{
    [Fact]
    public void LibraryLoadsWithoutErrors()
    {
        Assert.Empty(Repo.Kb.Errors);
        Assert.True(Repo.Kb.Problems.Count >= 50, $"solo hay {Repo.Kb.Problems.Count} fichas");
    }

    [Fact]
    public void EveryFactProbeAndActionReferencedExists()
    {
        var errors = Repo.Kb.ValidateAgainst(WindowsPlatform.Probes(), WindowsPlatform.Actions());
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    /// <summary>Páginas de Configuración verificadas contra la lista oficial de Microsoft (learn.microsoft.com, «Launch Windows Settings», 26/09/2026).</summary>
    private static readonly HashSet<string> VerifiedSettingsPages =
    [
        "activation", "appsfeatures", "apps-volume", "dateandtime", "display", "easeofaccess-display", "network-airplanemode",
        "network-proxy", "network-status", "network-wifi", "powersleep", "printers", "privacy-microphone", "privacy-webcam",
        "recovery", "regionlanguage", "sound", "startupapps", "storagesense", "windowsdefender", "windowsinsider", "windowsupdate",
        "windowsupdate-optionalupdates", "bluetooth", "disksandvolumes", "mobile-devices", "signinoptions", "defaultapps", "devices-touchpad", "easeofaccess-narrator", "nightlight", "clipboard", "easeofaccess-mousepointer", "easeofaccess-keyboard", "taskbar", "network-wifisettings", "network-mobilehotspot", "easeofaccess-magnifier", "notifications", "themes", "personalization-background", "multitasking",
    ];

    [Fact]
    public void EverySettingsPageOpenedIsOnMicrosoftsOfficialList()
    {
        var used = Repo.Kb.Problems.SelectMany(p => p.Pasos).Select(s => s.Abrir).OfType<string>()
            .Where(a => a.StartsWith("ms-settings:")).Select(a => a["ms-settings:".Length..]).Distinct();
        Assert.All(used, page => Assert.Contains(page, VerifiedSettingsPages));
    }

    [Fact]
    public void EveryPhraseLeadsBackToItsOwnProblem()
    {
        var matcher = new SymptomMatcher(Repo.Kb);
        var misses = new List<string>();
        foreach (var p in Repo.Kb.Problems)
            foreach (var phrase in p.Frases)
                if (!matcher.Match(phrase, 5).Any(m => m.Problem.Id == p.Id))
                    misses.Add($"{p.Id}: «{phrase}» → [{string.Join(", ", matcher.Match(phrase, 5).Select(m => m.Problem.Id))}]");
        Assert.True(misses.Count == 0, string.Join("\n", misses));
    }

    [Fact]
    public void EveryProblemOffersAWayOut()
    {
        foreach (var p in Repo.Kb.Problems)
            Assert.True(p.Acciones.Count > 0 || p.Pasos.Count > 0 || p.Derivar is not null, p.Id);
    }
}

/// <summary>
/// "Laboratorio de averías" a nivel de hechos: se parte de una PC sana, se inyecta una falla en los datos
/// y se comprueba que el motor encuentra exactamente lo esperado (y nada inventado).
/// </summary>
public class ScenarioTests
{
    private static readonly HashSet<string> AllProbes = WindowsPlatform.Probes().Select(p => p.Id).ToHashSet();

    internal static FactStore Healthy()
    {
        var f = new FactStore();
        void S(string k, object v) => f.Set(k, v);
        S("sistema.nombre", "Windows 11 Pro"); S("sistema.windows", 11); S("sistema.build", 26200); S("sistema.version", "25H2");
        S("sistema.edicion", "Professional"); S("sistema.arquitectura", "X64"); S("sistema.insider", false);
        S("sistema.fin_soporte", "12/10/2027"); S("sistema.fuera_de_soporte", false); S("sistema.dias_para_fin_soporte", 345);
        S("sistema.horas_encendida", 20); S("sistema.reinicio_pendiente", false); S("sistema.es_admin", false);
        S("licencia.activado", true); S("licencia.estado", 1); S("licencia.estado_texto", "activado");
        S("plataforma.uefi", true); S("plataforma.secure_boot", true); S("plataforma.tpm_presente", true); S("plataforma.tpm_version", 2);
        S("plataforma.bitlocker_sistema", false);
        S("cpu.nombre", "CPU"); S("cpu.uso_pct", 10); S("cpu.proceso_top", "x"); S("cpu.proceso_top_pct", 3); S("cpu.avx2", true);
        S("ram.total_gb", 16); S("ram.disponible_gb", 9); S("ram.uso_pct", 40); S("ram.modulos", 2); S("ram.ranuras", 2);
        S("ram.tipo", "DDR4"); S("ram.soldada_lpddr", false); S("ram.velocidad_diferencia", 0); S("ram.proceso_top", "x"); S("ram.proceso_top_gb", 1);
        S("disco.sistema.libre_gb", 200); S("disco.sistema.libre_pct", 40); S("disco.sistema.tipo", "SSD"); S("disco.alguno_falla", false);
        S("disco.alguno_advertencia", false); S("disco.sistema.temperatura", 40); S("disco.sistema.desgaste_pct", 5); S("disco.temporales_gb", 0.2);
        S("gpu.adaptador_basico", false); S("gpu.driver_antiguedad_dias", 100); S("gpu.nombre_principal", "GPU"); S("pantalla.hz_desaprovechada", false);
        S("bateria.presente", false);
        foreach (var k in new[] { "apagados_inesperados_30d", "pantallas_azules_30d", "whea_fatales_30d", "whea_corregidos_30d", "errores_disco_30d", "reinicios_video_30d", "programa_mas_fallado_veces", "cuelgues_30d" })
            S($"eventos.{k}", 0);
        S("dispositivos.con_error", 0); S("dispositivos.deshabilitados", 0);
        S("red.adaptador_conectado", true); S("red.apipa", false); S("red.puerta_enlace", "192.168.0.1"); S("red.puerta_enlace_ok", true);
        S("red.externo_ok", true); S("red.dns_ok", true); S("red.dns_servidores", "192.168.0.1"); S("red.web", "ok"); S("red.proxy_activado", false);
        S("hora.desfase_seg", 1);
        S("seguridad.antivirus_activo", true); S("seguridad.antivirus_nombres", "Windows Defender"); S("seguridad.antivirus_actualizado", true);
        S("seguridad.defender_activo", true); S("seguridad.defender_firmas_dias", 0); S("seguridad.firewall_windows_activo", true);
        S("seguridad.firewall_terceros_activo", false); S("seguridad.acceso_remoto_cantidad", 0); S("seguridad.hosts_entradas", 0);
        S("inicio.activos", 5);
        foreach (var s in new[] { "impresion", "audio", "update", "bits" }) { S($"servicios.{s}_ejecutando", true); S($"servicios.{s}_deshabilitado", false); }
        return f;
    }

    private static List<Finding> Run(Action<FactStore>? fault = null, ISet<string>? ran = null, IReadOnlyList<ProblemDef>? symptoms = null)
    {
        var facts = Healthy();
        fault?.Invoke(facts);
        return DiagnosticEngine.Evaluate(Repo.Kb, facts, ran ?? AllProbes, symptoms);
    }

    private static Finding Single(List<Finding> findings, string id) =>
        Assert.Single(findings, f => f.Problem.Id == id);

    [Fact]
    public void HealthyPcProducesNoFindingsAtAll() => Assert.Empty(Run());

    [Fact]
    public void BrokenDnsIsDiagnosedAsDnsWithItsRepair()
    {
        var findings = Run(f => f.Set("red.dns_ok", false));
        var dns = Single(findings, "NET-DNS-001");
        Assert.Equal(HealthStatus.Problem, dns.Status);
        Assert.Contains("net.dns.flush", dns.Problem.Acciones);
        Assert.DoesNotContain(findings, x => x.Problem.Id is "NET-003" or "NET-004");
    }

    [Fact]
    public void ProviderOutageIsNotBlamedOnThePc()
    {
        var findings = Run(f => { f.Set("red.externo_ok", false); f.Set("red.dns_ok", false); f.Set("red.web", "falla"); });
        Assert.Equal(HealthStatus.Problem, Single(findings, "NET-004").Status);
        Assert.DoesNotContain(findings, x => x.Problem.Id is "NET-DNS-001" or "NET-003");
    }

    [Fact]
    public void DeadRouterIsDistinguishedFromProvider()
    {
        var findings = Run(f => { f.Set("red.puerta_enlace_ok", false); f.Set("red.externo_ok", false); f.Set("red.dns_ok", false); f.Set("red.web", "falla"); });
        Single(findings, "NET-003");
        Assert.DoesNotContain(findings, x => x.Problem.Id == "NET-004");
    }

    [Fact]
    public void RouterIgnoringPingIsNotAProblemWhenInternetWorks()
    {
        Assert.Empty(Run(f => f.Set("red.puerta_enlace_ok", false)));
    }

    [Fact]
    public void FailingDiskAsksForPhysicalCheckAndBackup()
    {
        var findings = Run(f => { f.Set("disco.alguno_falla", true); f.Set("disco.discos_con_problema", "WD (en mal estado)"); });
        var disk = Single(findings, "HW-DSK-001");
        Assert.Equal(HealthStatus.NeedsPhysicalCheck, disk.Status);
        Assert.Contains("WD (en mal estado)", disk.Message);
    }

    [Fact]
    public void Windows10OutOfSupport()
    {
        var findings = Run(f => { f.Set("sistema.windows", 10); f.Set("sistema.fuera_de_soporte", true); f.Set("sistema.fin_soporte", "14/10/2025"); });
        Assert.Equal(HealthStatus.Warning, Single(findings, "WIN-LIFE-001").Status); // puede estar en ESU: no se afirma más de lo que se sabe
        Assert.DoesNotContain(findings, x => x.Problem.Id == "WIN-LIFE-002");
    }

    [Fact]
    public void Windows11NearEndOfSupportIsAWarningWithDays()
    {
        var w = Single(Run(f => f.Set("sistema.dias_para_fin_soporte", 30)), "WIN-LIFE-002");
        Assert.Equal(HealthStatus.Warning, w.Status);
        Assert.Contains("30 días", w.Message);
    }

    [Fact]
    public void StoppedAudioServiceIsAProblemWithItsRepair()
    {
        var a = Single(Run(f => f.Set("servicios.audio_ejecutando", false)), "AUD-002");
        Assert.Equal(HealthStatus.Problem, a.Status);
        Assert.Contains("audio.service.repair", a.Problem.Acciones);
    }

    [Fact]
    public void DisabledWindowsUpdateIsDetected()
    {
        Single(Run(f => f.Set("servicios.update_deshabilitado", true)), "WIN-UPD-020");
    }

    [Fact]
    public void NetworkThatBlocksTestServersButBrowsesFineIsNotAProblem()
    {
        Assert.Empty(Run(f => f.Set("red.externo_ok", false))); // la web funciona: no se acusa al proveedor
    }

    [Fact]
    public void SecurityCenterSilentButDefenderActiveIsNotAProblem()
    {
        Assert.Empty(Run(f => f.Set("seguridad.antivirus_activo", false)));
    }

    [Fact]
    public void XmpSuggestionOnlyOnDesktops()
    {
        Assert.Empty(Run(f => { f.Set("ram.velocidad_diferencia", 800); f.Set("ram.es_escritorio", false); }));
        Single(Run(f => { f.Set("ram.velocidad_diferencia", 800); f.Set("ram.es_escritorio", true); }), "HW-RAM-003");
    }

    [Fact]
    public void HugeDiskWithLowPercentageButPlentyOfSpaceIsFine()
    {
        Assert.Empty(Run(f => { f.Set("disco.sistema.libre_gb", 150); f.Set("disco.sistema.libre_pct", 4); }));
    }

    [Fact]
    public void PartialAnalysisDoesNotJudgeWhatItDidNotMeasure()
    {
        var facts = new FactStore();
        facts.Set("red.adaptador_conectado", true);
        facts.Set("red.puerta_enlace_ok", true);
        facts.Set("red.externo_ok", true);
        facts.Set("red.dns_ok", false);
        var findings = DiagnosticEngine.Evaluate(Repo.Kb, facts, new HashSet<string> { "red" });
        Assert.All(findings, f => Assert.Equal("red", f.Problem.Sondas[0]));
        Single(findings, "NET-DNS-001");
    }

    [Fact]
    public void MissingDataBecomesNotVerifiedInsteadOfAFakeProblem()
    {
        var findings = Run(f => { f.Set("seguridad.antivirus_activo", null); f.Set("seguridad.defender_activo", null); });
        var av = Single(findings, "SEC-AV-001");
        Assert.Equal(HealthStatus.Unverified, av.Status);
    }

    [Fact]
    public void SymptomOnlyEntriesAppearOnlyWhenTheUserMentionsThem()
    {
        Assert.DoesNotContain(Run(), f => f.Problem.Id == "AUD-001");
        var findings = Run(symptoms: [Repo.Kb.Get("AUD-001")!]);
        Assert.True(Single(findings, "AUD-001").FromSymptom);
    }
}

public class SymptomMatcherTests
{
    private static readonly SymptomMatcher Matcher = new(Repo.Kb);

    [Theory]
    [InlineData("no me sale la @", "BAS-KB-001")]
    [InlineData("no me sale la arroba en el teclado", "BAS-KB-001")]
    [InlineData("no se escucha nada", "AUD-001")]
    [InlineData("la compu no tiene sonido", "AUD-001")]
    [InlineData("me llamaron de microsoft y me hicieron instalar anydesk", "SCAM-002")]
    [InlineData("la impresora no imprime", "PRN-001")]
    [InlineData("se me apaga sola la compu", "WIN-PWR-010")]
    [InlineData("las letras son muy chiquitas", "BAS-TXT-001")]
    [InlineData("creo que tengo un virus", "SEC-VIR-001")]
    [InlineData("me quedé sin espacio en el disco", "FIL-SPC-001")]
    [InlineData("WhatsApp anda pero Google no", "NET-DNS-001")]
    [InlineData("me sale pantalla azul", "WIN-BSOD-001")]
    [InlineData("la batería me dura poco", "LAP-BAT-001")]
    [InlineData("el juego anda lento, pocos fps", "GAM-001")]
    public void FindsTheRightProblemInTop3(string text, string expectedId)
    {
        var top = Matcher.Match(text, 3).Select(m => m.Problem.Id).ToList();
        Assert.True(top.Contains(expectedId), $"«{text}» → [{string.Join(", ", top)}]");
    }

    [Theory]
    [InlineData("no se oye nada", "AUD-001")]                 // «se oye» = sonido de la PC
    [InlineData("mi hija dice que no me oye", "PER-MIC-001")]  // «no me oye» = micrófono
    [InlineData("no me ven en la llamada", "PER-CAM-001")]     // «no me ven» = cámara
    [InlineData("salió algo celeste con una carita triste", "WIN-BSOD-001")]
    [InlineData("la compu no se pone al día", "WIN-UPD-001")]
    public void UnderstandsEverydayExpressions(string text, string expectedId)
    {
        var top = Matcher.Match(text, 3).Select(m => m.Problem.Id).ToList();
        Assert.True(top.Contains(expectedId), $"«{text}» → [{string.Join(", ", top)}]");
    }

    [Fact]
    public void UnderstandsTwoProblemsInOneSentence()
    {
        var top = Matcher.Match("Mi computadora está muy lenta y no puedo abrir Word", 5).Select(m => m.Problem.Id).ToList();
        Assert.Contains("PERF-SLOW-001", top);
        Assert.Contains("APP-OFF-001", top);
    }

    [Fact]
    public void InternetComplaintsGoToNetworkProblems()
    {
        var top = Matcher.Match("no me anda internet", 3).Select(m => m.Problem.Id).ToList();
        Assert.NotEmpty(top);
        Assert.All(top, id => Assert.StartsWith("NET-", id));
    }

    [Theory]
    [InlineData("hola")]
    [InlineData("gracias")]
    [InlineData("")]
    public void DoesNotInventMatchesForUnrelatedText(string text) => Assert.Empty(Matcher.Match(text));
}
