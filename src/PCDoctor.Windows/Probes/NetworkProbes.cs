using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using PCDoctor.Core;

namespace PCDoctor.Windows.Probes;

/// <summary>
/// Diagnóstico de red por capas: adaptador → IP → router → Internet → DNS → web. Así se distingue
/// si falla la PC, el router o el proveedor. Usa ping Y conexiones TCP, porque algunas redes bloquean el ping.
/// </summary>
internal sealed class NetworkProbe : ProbeBase
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string[] VirtualHints = ["virtual", "hyper-v", "vmware", "virtualbox", "vethernet", "tap-", "wireguard", "tailscale", "zerotier", "loopback", "bluetooth"];

    public override string Id => "red";
    public override string Title => "Conexión a Internet";
    public override IReadOnlyList<string> Areas => [Core.Areas.Network, Core.Areas.Security];
    public override IReadOnlyList<string> FactKeys =>
    [
        "red.adaptador_conectado", "red.tipo", "red.apipa", "red.puerta_enlace", "red.puerta_enlace_ok", "red.externo_ok",
        "red.dns_ok", "red.dns_servidores", "red.web", "red.proxy_activado", "red.proxy_detalle",
    ];

    protected override async Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        var all = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();
        bool IsVirtual(NetworkInterface n) => VirtualHints.Any(h => (n.Description + " " + n.Name).Contains(h, StringComparison.OrdinalIgnoreCase));
        var up = all.Where(n => n.OperationalStatus == OperationalStatus.Up).ToList();
        var physicalUp = up.Where(n => !IsVirtual(n)).ToList();

        NetworkInterface? main = null;
        IPAddress? gateway = null;
        foreach (var n in physicalUp.Concat(up.Where(IsVirtual)))
        {
            var gw = n.GetIPProperties().GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gw is null) continue;
            main = n; gateway = gw; break;
        }
        main ??= physicalUp.FirstOrDefault();

        r.Facts.Set("red.adaptador_conectado", physicalUp.Count > 0 || gateway is not null);
        var apipa = physicalUp.Any(n => n.GetIPProperties().UnicastAddresses
            .Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)))
            && gateway is null;
        r.Facts.Set("red.apipa", apipa);

        if (main is not null)
        {
            var type = main.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : main.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet (cable)" : main.NetworkInterfaceType.ToString();
            r.Facts.Set("red.tipo", type);
            r.Detail("Conexión usada", $"{type}: {main.Description}");
            var ips = main.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address.ToString());
            r.Detail("Dirección IP", string.Join(", ", ips));
            var dns = main.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
            if (dns.Count > 0) { r.Facts.Set("red.dns_servidores", string.Join(", ", dns)); r.Detail("Servidores DNS", string.Join(", ", dns)); }
        }
        else
        {
            r.Detail("Conexión usada", "ninguna conectada");
        }

        if (gateway is not null)
        {
            r.Facts.Set("red.puerta_enlace", gateway.ToString());
            var gwOk = await PingAsync(gateway, ct) || await TcpAnyAsync([(gateway, 53), (gateway, 80), (gateway, 443)], ct);
            r.Facts.Set("red.puerta_enlace_ok", gwOk);
            r.Detail("Router (puerta de enlace)", $"{gateway} · {(gwOk ? "responde" : "no responde")}");
        }

        var externalOk = await TcpAnyAsync([(IPAddress.Parse("1.1.1.1"), 443), (IPAddress.Parse("8.8.8.8"), 53), (IPAddress.Parse("9.9.9.9"), 443)], ct);
        r.Facts.Set("red.externo_ok", externalOk);
        r.Detail("Salida a Internet", externalOk ? "funciona" : "no hay salida");

        var dnsOk = await ResolveAnyAsync(["www.microsoft.com", "www.google.com", "www.wikipedia.org"], ct);
        r.Facts.Set("red.dns_ok", dnsOk);
        r.Detail("Traducción de nombres (DNS)", dnsOk ? "funciona" : "falla");

        var web = dnsOk ? await NcsiAsync(ct) : "falla";
        r.Facts.Set("red.web", web);
        r.Detail("Acceso a páginas web", web switch { "ok" => "funciona", "portal" => "la red pide iniciar sesión (portal de acceso)", _ => "falla" });

        var proxyEnable = Reg.Int(RegistryHive.CurrentUser, InternetSettings, "ProxyEnable") == 1;
        var proxyServer = Reg.Str(RegistryHive.CurrentUser, InternetSettings, "ProxyServer");
        var pac = Reg.Str(RegistryHive.CurrentUser, InternetSettings, "AutoConfigURL");
        var proxyOn = proxyEnable || !string.IsNullOrWhiteSpace(pac);
        r.Facts.Set("red.proxy_activado", proxyOn);
        if (proxyOn)
        {
            var detail = string.Join(" · ", new[] { proxyEnable ? $"servidor {proxyServer}" : null, string.IsNullOrWhiteSpace(pac) ? null : $"script {pac}" }.Where(s => s is not null));
            r.Facts.Set("red.proxy_detalle", detail);
            r.Detail("Proxy", detail);
        }
        else
        {
            r.Detail("Proxy", "no configurado");
        }
    }

    private static async Task<bool> PingAsync(IPAddress ip, CancellationToken ct)
    {
        using var ping = new Ping();
        for (var i = 0; i < 2; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(1200), cancellationToken: ct);
                if (reply.Status == IPStatus.Success) return true;
            }
            catch (PingException) { }
        }
        return false;
    }

    internal static async Task<bool> TcpAnyAsync(IEnumerable<(IPAddress ip, int port)> targets, CancellationToken ct)
    {
        var tasks = targets.Select(async t =>
        {
            using var client = new TcpClient(t.ip.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2500);
            try { await client.ConnectAsync(t.ip, t.port, cts.Token); return true; }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { return false; }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        return results.Any(x => x);
    }

    internal static async Task<bool> ResolveAnyAsync(IEnumerable<string> hosts, CancellationToken ct)
    {
        foreach (var h in hosts)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(4000);
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(h, cts.Token);
                if (addrs.Length > 0) return true;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { }
        }
        return false;
    }

    /// <summary>Misma prueba que usa Windows para saber si hay Internet (NCSI). Detecta portales cautivos.</summary>
    private static async Task<string> NcsiAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            using var resp = await http.GetAsync("http://www.msftconnecttest.com/connecttest.txt", ct);
            if ((int)resp.StatusCode is >= 300 and < 400) return "portal";
            var body = await resp.Content.ReadAsStringAsync(ct);
            return body.Trim() == "Microsoft Connect Test" ? "ok" : "portal";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return "falla";
        }
    }
}

/// <summary>Compara el reloj de la PC con un servidor de hora (SNTP). Un reloj desfasado rompe las páginas seguras.</summary>
internal sealed class TimeProbe : ProbeBase
{
    public override string Id => "hora";
    public override string Title => "Fecha y hora";
    public override IReadOnlyList<string> Areas => [Core.Areas.Network, Core.Areas.Windows];
    public override IReadOnlyList<string> FactKeys => ["hora.desfase_seg", "hora.zona"];

    protected override async Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        r.Facts.Set("hora.zona", TimeZoneInfo.Local.DisplayName);
        r.Detail("Zona horaria", TimeZoneInfo.Local.DisplayName);
        await PartAsync(r, "Comparación con la hora de Internet", async () =>
        {
            var offset = await SntpOffsetAsync("time.windows.com", ct) ?? await SntpOffsetAsync("pool.ntp.org", ct)
                         ?? throw new InvalidOperationException("sin respuesta de los servidores de hora");
            r.Facts.Set("hora.desfase_seg", Math.Round(Math.Abs(offset.TotalSeconds)));
            r.Detail("Diferencia con la hora real", $"{Math.Abs(offset.TotalSeconds):0} segundos");
        });
    }

    internal static async Task<TimeSpan?> SntpOffsetAsync(string server, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(3000);
            var req = new byte[48];
            req[0] = 0x1B; // LI=0, versión 3, modo cliente
            var addresses = await Dns.GetHostAddressesAsync(server, AddressFamily.InterNetwork, cts.Token);
            if (addresses.Length == 0) return null;
            var sw = Stopwatch.StartNew();
            var sent = DateTime.UtcNow;
            await udp.SendAsync(req, new IPEndPoint(addresses[0], 123), cts.Token);
            var resp = await udp.ReceiveAsync(cts.Token);
            var roundTrip = sw.Elapsed;
            if (resp.Buffer.Length < 48) return null;
            var leap = resp.Buffer[0] >> 6;
            var stratum = resp.Buffer[1];
            if (leap == 3 || stratum is 0 or > 15) return null; // servidor no sincronizado o que rechaza la consulta
            ulong secs = (ulong)resp.Buffer[40] << 24 | (ulong)resp.Buffer[41] << 16 | (ulong)resp.Buffer[42] << 8 | resp.Buffer[43];
            ulong frac = (ulong)resp.Buffer[44] << 24 | (ulong)resp.Buffer[45] << 16 | (ulong)resp.Buffer[46] << 8 | resp.Buffer[47];
            if (secs == 0) return null;
            var serverTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs + frac / 4294967296.0);
            var localAtReply = sent + roundTrip / 2;
            return serverTime - localAtReply;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return null;
        }
    }
}
