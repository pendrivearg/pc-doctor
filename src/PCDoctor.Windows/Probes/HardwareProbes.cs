using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCDoctor.Core;

namespace PCDoctor.Windows.Probes;

/// <summary>Procesador: modelo, núcleos, instrucciones, virtualización y uso real medido durante 3 segundos.</summary>
internal sealed class CpuProbe : ProbeBase
{
    public override string Id => "cpu";
    public override string Title => "Procesador";
    public override bool RunsAlone => true;
    public override IReadOnlyList<string> Areas => [Core.Areas.Hardware, Core.Areas.Performance];
    public override IReadOnlyList<string> FactKeys =>
        ["cpu.nombre", "cpu.nucleos", "cpu.hilos", "cpu.mhz_max", "cpu.avx2", "cpu.virtualizacion_bios", "cpu.uso_pct", "cpu.proceso_top", "cpu.proceso_top_pct"];

    protected override async Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        Part(r, "Datos del procesador", () =>
        {
            var cpu = Wmi.Cimv2("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, VirtualizationFirmwareEnabled FROM Win32_Processor");
            var first = cpu.First();
            var name = Regex.Replace(first.Str("Name"), @"\s+", " ");
            r.Facts.Set("cpu.nombre", name);
            r.Facts.Set("cpu.nucleos", cpu.Sum(c => (double)c.Get<uint>("NumberOfCores")));
            r.Facts.Set("cpu.hilos", cpu.Sum(c => (double)c.Get<uint>("NumberOfLogicalProcessors")));
            r.Facts.Set("cpu.mhz_max", first.Get<uint>("MaxClockSpeed"));
            var virt = first.Get<object>("VirtualizationFirmwareEnabled");
            if (virt is bool v) r.Facts.Set("cpu.virtualizacion_bios", v);
            r.Detail("Modelo", name);
            r.Detail("Núcleos / hilos", $"{r.Facts.All["cpu.nucleos"]} / {r.Facts.All["cpu.hilos"]}");
            r.Detail("Frecuencia base informada", $"{first.Get<uint>("MaxClockSpeed")} MHz");
            if (virt is bool vv) r.Detail("Virtualización habilitada en BIOS", vv);
        });
        var avx2 = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86 && Avx2.IsSupported;
        r.Facts.Set("cpu.avx2", avx2);
        r.Detail("Instrucciones AVX2", avx2);

        await PartAsync(r, "Uso del procesador", async () =>
        {
            var before = CpuTimes();
            if (before is null) throw new InvalidOperationException("GetSystemTimes falló");
            var procBefore = ProcessTimes();
            await Task.Delay(3000, ct);
            var after = CpuTimes()!.Value;
            var procAfter = ProcessTimes();
            var idle = after.idle - before.Value.idle;
            var total = (after.kernel - before.Value.kernel) + (after.user - before.Value.user); // kernel incluye idle
            var usage = total <= 0 ? 0 : Math.Clamp(100.0 * (total - idle) / total, 0, 100);
            r.Facts.Set("cpu.uso_pct", Math.Round(usage));
            r.Detail("Uso medido (3 s)", $"{usage:0} %");

            var wall = TimeSpan.FromSeconds(3).TotalMilliseconds * Environment.ProcessorCount;
            var top = procAfter
                .Where(kv => procBefore.ContainsKey(kv.Key) && kv.Value.name is not ("Idle" or "System"))
                .Select(kv => (kv.Value.name, pct: 100.0 * (kv.Value.cpu - procBefore[kv.Key].cpu).TotalMilliseconds / wall))
                .GroupBy(x => x.name)
                .Select(g => (name: g.Key, pct: g.Sum(x => x.pct)))
                .OrderByDescending(x => x.pct)
                .FirstOrDefault();
            if (top.name is not null)
            {
                r.Facts.Set("cpu.proceso_top", top.name);
                r.Facts.Set("cpu.proceso_top_pct", Math.Round(top.pct));
                r.Detail("Programa que más procesador usa", $"{top.name} ({top.pct:0} %)");
            }
        });
    }

    private static (long idle, long kernel, long user)? CpuTimes() =>
        Native.GetSystemTimes(out var i, out var k, out var u) ? (i, k, u) : null;

    private static Dictionary<int, (string name, TimeSpan cpu)> ProcessTimes()
    {
        var map = new Dictionary<int, (string, TimeSpan)>();
        foreach (var p in Process.GetProcesses())
        {
            try { map[p.Id] = (p.ProcessName, p.TotalProcessorTime); }
            catch { /* procesos protegidos: sin acceso, se ignoran */ }
            finally { p.Dispose(); }
        }
        return map;
    }
}

/// <summary>Memoria RAM: uso real, módulos, ranuras, tipo y velocidades.</summary>
internal sealed class MemoryProbe : ProbeBase
{
    public override string Id => "ram";
    public override string Title => "Memoria RAM";
    public override IReadOnlyList<string> Areas => [Core.Areas.Hardware, Core.Areas.Performance];
    public override IReadOnlyList<string> FactKeys =>
    [
        "ram.total_gb", "ram.disponible_gb", "ram.uso_pct", "ram.modulos", "ram.ranuras", "ram.tipo", "ram.soldada_lpddr",
        "ram.velocidad_nominal", "ram.velocidad_configurada", "ram.velocidad_diferencia", "ram.max_gb", "ram.proceso_top", "ram.proceso_top_gb",
        "ram.es_escritorio",
    ];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("GlobalMemoryStatusEx falló");
        var total = Gb(status.ullTotalPhys);
        var avail = Gb(status.ullAvailPhys);
        var used = Math.Round(100.0 * (status.ullTotalPhys - status.ullAvailPhys) / status.ullTotalPhys);
        r.Facts.Set("ram.total_gb", total);
        r.Facts.Set("ram.disponible_gb", avail);
        r.Facts.Set("ram.uso_pct", used);
        r.Detail("Memoria total utilizable", $"{Labels.Number(total)} GB");
        r.Detail("En uso", $"{used} % (libres {Labels.Number(avail)} GB)");

        Part(r, "Módulos de memoria", () =>
        {
            var modules = Wmi.Cimv2("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor, DeviceLocator FROM Win32_PhysicalMemory");
            r.Facts.Set("ram.modulos", modules.Count);
            if (modules.Count > 0)
            {
                var type = modules.Select(m => (int)m.Get<uint>("SMBIOSMemoryType")).First();
                var typeName = type switch
                {
                    20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5",
                    27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 35 => "LPDDR5",
                    _ => "",
                };
                if (typeName.Length > 0) r.Facts.Set("ram.tipo", typeName);
                if (typeName.Length > 0) r.Facts.Set("ram.soldada_lpddr", typeName.StartsWith("LP", StringComparison.Ordinal));
                var nominal = modules.Select(m => (double)m.Get<uint>("Speed")).Where(s => s > 0).DefaultIfEmpty(0).Min();
                var configured = modules.Select(m => (double)m.Get<uint>("ConfiguredClockSpeed")).Where(s => s > 0).DefaultIfEmpty(0).Min();
                if (nominal > 0) r.Facts.Set("ram.velocidad_nominal", nominal);
                if (configured > 0) r.Facts.Set("ram.velocidad_configurada", configured);
                if (nominal > 0 && configured > 0) r.Facts.Set("ram.velocidad_diferencia", nominal - configured);
                foreach (var m in modules)
                    r.Detail($"Módulo {m.Str("DeviceLocator")}", $"{Gb(m.Get<ulong>("Capacity"))} GB {typeName} {m.Get<uint>("ConfiguredClockSpeed")} MT/s");
            }
            var arrays = Wmi.Cimv2("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray WHERE Use = 3");
            if (arrays.Count > 0)
            {
                var slots = arrays.Sum(a => (double)a.Get<uint>("MemoryDevices"));
                var maxKb = arrays.Sum(a => (double)a.Get<ulong>("MaxCapacityEx"));
                if (slots > 0) r.Facts.Set("ram.ranuras", slots);
                if (maxKb > 0) r.Facts.Set("ram.max_gb", Math.Round(maxKb / 1024 / 1024));
                r.Detail("Ranuras informadas por la BIOS", slots);
                if (maxKb > 0) r.Detail("Capacidad máxima informada por la BIOS (puede ser inexacta)", $"{Math.Round(maxKb / 1024 / 1024)} GB");
            }
        });

        Part(r, "Tipo de equipo", () =>
        {
            // SMBIOS ChassisTypes: 3-7, 13, 15, 16, 35, 36 = escritorio; 8-10, 14, 30-32 = portátil.
            var chassis = Wmi.Cimv2("SELECT ChassisTypes FROM Win32_SystemEnclosure").SelectMany(e => e.Get<ushort[]>("ChassisTypes") ?? []).ToList();
            ushort[] desktop = [3, 4, 5, 6, 7, 13, 15, 16, 35, 36];
            ushort[] laptop = [8, 9, 10, 14, 30, 31, 32];
            if (chassis.Any(desktop.Contains)) r.Facts.Set("ram.es_escritorio", true);
            else if (chassis.Any(laptop.Contains)) r.Facts.Set("ram.es_escritorio", false);
        });

        Part(r, "Programa que más memoria usa", () =>
        {
            var top = Process.GetProcesses()
                .Select(p => { try { return (p.ProcessName, ws: p.WorkingSet64); } catch { return (p.ProcessName, ws: 0L); } finally { p.Dispose(); } })
                .GroupBy(x => x.ProcessName)
                .Select(g => (name: g.Key, gb: Gb(g.Sum(x => (double)x.ws))))
                .OrderByDescending(x => x.gb)
                .First();
            r.Facts.Set("ram.proceso_top", top.name);
            r.Facts.Set("ram.proceso_top_gb", top.gb);
            r.Detail("Programa que más memoria usa", $"{top.name} ({Labels.Number(top.gb)} GB)");
        });
        return Task.CompletedTask;
    }
}

/// <summary>Discos: espacio, tipo, estado de salud que informa el propio disco, y (con permiso) temperatura y desgaste.</summary>
internal sealed class DiskProbe : ProbeBase
{
    private const string StorageNs = @"root\Microsoft\Windows\Storage";

    public override string Id => "disco";
    public override string Title => "Discos y espacio";
    public override IReadOnlyList<string> Areas => [Core.Areas.Hardware, Core.Areas.Performance, Core.Areas.Windows];
    public override IReadOnlyList<string> FactKeys =>
    [
        "disco.sistema.libre_gb", "disco.sistema.total_gb", "disco.sistema.libre_pct", "disco.sistema.tipo", "disco.sistema.bus",
        "disco.alguno_falla", "disco.alguno_advertencia", "disco.discos_con_problema", "disco.sistema.temperatura",
        "disco.sistema.desgaste_pct", "disco.temporales_gb",
    ];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var drive = new DriveInfo(sysRoot);
        var free = Gb(drive.AvailableFreeSpace);
        var total = Gb(drive.TotalSize);
        r.Facts.Set("disco.sistema.libre_gb", free);
        r.Facts.Set("disco.sistema.total_gb", total);
        r.Facts.Set("disco.sistema.libre_pct", Math.Round(100.0 * drive.AvailableFreeSpace / drive.TotalSize));
        r.Detail($"Espacio libre en {sysRoot.TrimEnd('\\')}", $"{Labels.Number(free)} GB de {Labels.Number(total)} GB");

        string? systemDiskNumber = null;
        Part(r, "Disco del sistema", () =>
        {
            var letter = sysRoot[0];
            var part = Wmi.Query(StorageNs, $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'");
            if (part.Count > 0) systemDiskNumber = part[0].Get<uint>("DiskNumber").ToString();
        });

        Part(r, "Estado de los discos", () =>
        {
            var disks = Wmi.Query(StorageNs, "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk");
            var bad = new List<string>();
            var warn = false;
            var fail = false;
            foreach (var d in disks)
            {
                var name = d.Str("FriendlyName");
                var media = d.Get<ushort>("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Desconocido" };
                var bus = BusName(d.Get<ushort>("BusType"));
                var health = d.Get<ushort>("HealthStatus"); // 0 sano, 1 advertencia, 2 en mal estado
                if (health == 1) { warn = true; bad.Add($"{name} (advertencia)"); }
                if (health == 2) { fail = true; bad.Add($"{name} (en mal estado)"); }
                var healthText = health switch { 0 => "sano", 1 => "advertencia", 2 => "en mal estado", _ => "desconocido" };
                r.Detail($"Disco {d.Str("DeviceId")}: {name}", $"{media} {bus} · {Gb(d.Get<ulong>("Size"))} GB · estado informado: {healthText}");
                if (d.Str("DeviceId") == systemDiskNumber)
                {
                    // Algunos SSD SATA se informan como "Desconocido"; si el bus es NVMe es un SSD con certeza.
                    if (media == "Desconocido" && bus == "NVMe") media = "SSD";
                    r.Facts.Set("disco.sistema.tipo", media);
                    r.Facts.Set("disco.sistema.bus", bus);
                }
            }
            r.Facts.Set("disco.alguno_falla", fail);
            r.Facts.Set("disco.alguno_advertencia", warn);
            if (bad.Count > 0) r.Facts.Set("disco.discos_con_problema", string.Join("; ", bad));
        });

        Part(r, "Temperatura y desgaste del disco del sistema", () =>
        {
            if (systemDiskNumber is null) throw new InvalidOperationException("no se identificó el disco del sistema");
            var disks = Wmi.Query(StorageNs, $"SELECT ObjectId FROM MSFT_PhysicalDisk WHERE DeviceId = '{systemDiskNumber}'");
            if (disks.Count == 0) throw new InvalidOperationException("disco no encontrado");
            var objectId = disks[0].Str("ObjectId").Replace("\\", "\\\\").Replace("\"", "\\\"");
            var counters = Wmi.Query(StorageNs,
                $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{objectId}\"}} WHERE ResultClass = MSFT_StorageReliabilityCounter");
            if (counters.Count == 0)
                throw ctx.IsElevated ? new InvalidOperationException("el disco no informa estos datos") : new AccessDeniedException("contadores del disco");
            var c = counters[0];
            var temp = c.Get<ushort>("Temperature");
            var wear = c.Get<byte>("Wear");
            if (temp > 0) { r.Facts.Set("disco.sistema.temperatura", temp); r.Detail("Temperatura del disco del sistema", $"{temp} °C"); }
            if (c.Get<object>("Wear") is not null) { r.Facts.Set("disco.sistema.desgaste_pct", wear); r.Detail("Desgaste informado", $"{wear} %"); }
        });

        Part(r, "Archivos temporales", () =>
        {
            // Solo se mide (y se ofrece limpiar) la carpeta estándar; si TEMP apunta a otro lado, queda sin comprobar.
            var size = DirectorySize(new DirectoryInfo(Actions.CleanTempAction.SafeTempRoot()), TimeSpan.FromSeconds(6));
            r.Facts.Set("disco.temporales_gb", Math.Round(size / 1024.0 / 1024 / 1024, 2));
            r.Detail("Archivos temporales del usuario", $"{Labels.Number(Math.Round(size / 1024.0 / 1024 / 1024, 2))} GB");
        });
        return Task.CompletedTask;
    }

    private static string BusName(ushort bus) => bus switch
    {
        3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", 15 => "Virtual",
        _ => "",
    };

    internal static long DirectorySize(DirectoryInfo dir, TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        long total = 0;
        var options = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in dir.EnumerateFiles("*", options))
        {
            try { total += f.Length; } catch (IOException) { }
            if (sw.Elapsed > budget) break;
        }
        return total;
    }
}

/// <summary>Placas de video y pantallas: modelo, controlador, VRAM real, integrada/dedicada y frecuencia del monitor.</summary>
internal sealed class GpuProbe : ProbeBase
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public override string Id => "gpu";
    public override string Title => "Placa de video y pantalla";
    public override IReadOnlyList<string> Areas => [Core.Areas.Hardware];
    public override IReadOnlyList<string> FactKeys =>
    [
        "gpu.cantidad", "gpu.nombre_principal", "gpu.tiene_dedicada", "gpu.adaptador_basico", "gpu.driver_antiguedad_dias",
        "pantalla.hz_desaprovechada", "pantalla.detalle_hz",
    ];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        Part(r, "Placas de video", () =>
        {
            var gpus = Wmi.Cimv2("SELECT Name, DriverVersion, DriverDate, PNPDeviceID, InfFilename FROM Win32_VideoController");
            var vram = VramByName();
            var dedicated = false;
            var basic = false;
            string? primary = null;
            double? primaryAgeDays = null;
            foreach (var g in gpus)
            {
                var name = g.Str("Name");
                var pnp = g.Str("PNPDeviceID");
                var isBasic = g.Str("InfFilename").Equals("display.inf", StringComparison.OrdinalIgnoreCase)
                              || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                              || name.Contains("pantalla básico", StringComparison.OrdinalIgnoreCase);
                basic |= isBasic;
                var isDedicated = IsDedicated(name, pnp);
                dedicated |= isDedicated;
                DateTime? driverDate = null;
                var rawDate = g.Str("DriverDate");
                if (rawDate.Length >= 8) { try { driverDate = ManagementDateTimeConverter.ToDateTime(rawDate); } catch (ArgumentOutOfRangeException) { } }
                var desc = $"{(isDedicated ? "dedicada" : "integrada")} · controlador {g.Str("DriverVersion")}";
                if (driverDate is { } dd) desc += $" del {dd:dd/MM/yyyy}";
                if (vram.TryGetValue(name, out var bytes) && bytes > 0) desc += $" · {Gb(bytes)} GB de memoria de video";
                r.Detail(name, desc);
                // La "principal" para juegos es la dedicada si existe.
                if (primary is null || (isDedicated && !IsDedicated(primary, "")))
                {
                    primary = name;
                    primaryAgeDays = driverDate is { } d2 ? Math.Floor((DateTime.Today - d2).TotalDays) : null;
                }
            }
            r.Facts.Set("gpu.cantidad", gpus.Count);
            r.Facts.Set("gpu.tiene_dedicada", dedicated);
            r.Facts.Set("gpu.adaptador_basico", basic);
            if (primary is not null) r.Facts.Set("gpu.nombre_principal", primary);
            if (primaryAgeDays is { } age && !basic) r.Facts.Set("gpu.driver_antiguedad_dias", age);
        });

        Part(r, "Frecuencia de los monitores", () =>
        {
            var lines = new List<string>();
            var wasted = false;
            var dev = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dev, 0); i++, dev.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>())
            {
                if ((dev.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
                var current = new Native.DEVMODE { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>() };
                if (!Native.EnumDisplaySettings(dev.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref current)) continue;
                var max = current.dmDisplayFrequency;
                var mode = new Native.DEVMODE { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>() };
                for (var m = 0; Native.EnumDisplaySettings(dev.DeviceName, m, ref mode); m++)
                    if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmDisplayFrequency > max)
                        max = mode.dmDisplayFrequency;
                var label = $"{current.dmPelsWidth}×{current.dmPelsHeight} a {current.dmDisplayFrequency} Hz (admite hasta {max} Hz)";
                lines.Add(label);
                r.Detail($"Pantalla {dev.DeviceName.Replace(@"\\.\", "")}", label);
                if (max >= 100 && current.dmDisplayFrequency < max - 5) wasted = true;
            }
            if (lines.Count == 0) throw new InvalidOperationException("no se detectaron pantallas");
            r.Facts.Set("pantalla.hz_desaprovechada", wasted);
            r.Facts.Set("pantalla.detalle_hz", string.Join("; ", lines));
        });
        return Task.CompletedTask;
    }

    internal static bool IsDedicated(string name, string pnp)
    {
        if (pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(name, @"\b(RX|R[579]|HD|Pro|FirePro|W\d{4})\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(name, @"Radeon\(TM\) Graphics|Radeon Graphics|Vega \d+ Graphics", RegexOptions.IgnoreCase);
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(name, @"Arc\(TM\) [AB]\d{3}|Arc [AB]\d{3}", RegexOptions.IgnoreCase);
        return false;
    }

    private static Dictionary<string, long> VramByName()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cls = root.OpenSubKey(DisplayClass);
        if (cls is null) return map;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc") as string;
                if (desc is null) continue;
                var v = k!.GetValue("HardwareInformation.qwMemorySize") ?? k.GetValue("HardwareInformation.MemorySize");
                long bytes = v switch
                {
                    long l => l,
                    int i => (uint)i,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    _ => 0,
                };
                if (bytes > map.GetValueOrDefault(desc)) map[desc] = bytes;
            }
            catch (System.Security.SecurityException) { }
        }
        return map;
    }
}

/// <summary>Batería (notebooks): capacidad original vs actual.</summary>
internal sealed class BatteryProbe : ProbeBase
{
    public override string Id => "bateria";
    public override string Title => "Batería";
    public override IReadOnlyList<string> Areas => [Core.Areas.Hardware];
    public override IReadOnlyList<string> FactKeys => ["bateria.presente", "bateria.carga_pct", "bateria.salud_pct"];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        var batteries = Wmi.Cimv2("SELECT EstimatedChargeRemaining FROM Win32_Battery");
        r.Facts.Set("bateria.presente", batteries.Count > 0);
        if (batteries.Count == 0) { r.Detail("Batería", "no tiene (PC de escritorio o batería no detectada)"); return Task.CompletedTask; }
        r.Facts.Set("bateria.carga_pct", batteries[0].Get<ushort>("EstimatedChargeRemaining"));
        r.Detail("Carga actual", $"{batteries[0].Get<ushort>("EstimatedChargeRemaining")} %");
        Part(r, "Desgaste de la batería", () =>
        {
            var design = Wmi.Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData");
            var full = Wmi.Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            if (design.Count == 0 || full.Count == 0) throw new InvalidOperationException("la batería no informa su capacidad");
            var d = design.Sum(x => (double)x.Get<uint>("DesignedCapacity"));
            var f = full.Sum(x => (double)x.Get<uint>("FullChargedCapacity"));
            if (d <= 0) throw new InvalidOperationException("capacidad de diseño no informada");
            var health = Math.Round(Math.Min(100, 100 * f / d));
            r.Facts.Set("bateria.salud_pct", health);
            r.Detail("Capacidad actual frente a la original", $"{health} %");
        });
        return Task.CompletedTask;
    }
}
