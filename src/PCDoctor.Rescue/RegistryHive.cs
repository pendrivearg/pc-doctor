using System.Buffers.Binary;
using System.Text;

namespace PCDoctor.Rescue;

/// <summary>
/// Lector de solo lectura del formato de registro de Windows (regf), para revisar SOFTWARE, SYSTEM y NTUSER.DAT
/// de un Windows apagado. No escribe nunca. No aplica los registros de transacciones (.LOG1/.LOG2): si Windows
/// se apagó mal, los cambios de los últimos segundos pueden no verse.
/// </summary>
public sealed class RegistryHive
{
    private const int HbinStart = 0x1000;
    private readonly byte[] _data;
    private readonly int _rootCell;

    private RegistryHive(byte[] data)
    {
        _data = data;
        _rootCell = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x24));
    }

    public static RegistryHive? TryOpen(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < HbinStart + 32 || Encoding.ASCII.GetString(data, 0, 4) != "regf") return null;
            var hive = new RegistryHive(data);
            return hive.IsKey(hive._rootCell) ? hive : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public RegistryKeyView Root => new(this, _rootCell);

    /// <summary>Busca una clave por ruta relativa a la raíz de la colmena («Microsoft\Windows\CurrentVersion\Run»).</summary>
    public RegistryKeyView? Open(string path)
    {
        var key = Root;
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = key.SubKeys().FirstOrDefault(k => string.Equals(k.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next is null) return null;
            key = next;
        }
        return key;
    }

    private Span<byte> Cell(int offset)
    {
        var abs = HbinStart + offset;
        if (offset < 0 || abs + 4 > _data.Length) return Span<byte>.Empty;
        var size = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(abs)));
        if (size < 8 || abs + size > _data.Length) return Span<byte>.Empty;
        return _data.AsSpan(abs + 4, size - 4);
    }

    private bool IsKey(int offset)
    {
        var c = Cell(offset);
        return c.Length >= 0x4C && c[0] == (byte)'n' && c[1] == (byte)'k';
    }

    internal string KeyName(int offset)
    {
        var c = Cell(offset);
        int len = BinaryPrimitives.ReadUInt16LittleEndian(c[0x48..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(c[2..]);
        if (0x4C + len > c.Length) return "";
        var raw = c.Slice(0x4C, len);
        return (flags & 0x20) != 0 ? Encoding.Latin1.GetString(raw) : Encoding.Unicode.GetString(raw);
    }

    internal IEnumerable<int> SubKeyOffsets(int keyOffset)
    {
        var c = Cell(keyOffset);
        var count = BinaryPrimitives.ReadInt32LittleEndian(c[0x14..]);
        if (count <= 0) return [];
        var list = BinaryPrimitives.ReadInt32LittleEndian(c[0x1C..]);
        var result = new List<int>();
        CollectList(list, result, depth: 0);
        return result;
    }

    private void CollectList(int listOffset, List<int> into, int depth)
    {
        if (depth > 4) return;
        var c = Cell(listOffset);
        if (c.Length < 4) return;
        var sig = Encoding.ASCII.GetString(c[..2]);
        int n = BinaryPrimitives.ReadUInt16LittleEndian(c[2..]);
        switch (sig)
        {
            case "lf" or "lh":
                for (var i = 0; i < n && 4 + i * 8 + 4 <= c.Length; i++)
                    AddKey(BinaryPrimitives.ReadInt32LittleEndian(c[(4 + i * 8)..]), into);
                break;
            case "li":
                for (var i = 0; i < n && 4 + i * 4 + 4 <= c.Length; i++)
                    AddKey(BinaryPrimitives.ReadInt32LittleEndian(c[(4 + i * 4)..]), into);
                break;
            case "ri":
                for (var i = 0; i < n && 4 + i * 4 + 4 <= c.Length; i++)
                    CollectList(BinaryPrimitives.ReadInt32LittleEndian(c[(4 + i * 4)..]), into, depth + 1);
                break;
        }
    }

    private void AddKey(int offset, List<int> into)
    {
        if (IsKey(offset)) into.Add(offset);
    }

    internal IEnumerable<(string Name, object? Value)> Values(int keyOffset)
    {
        var c = Cell(keyOffset);
        var count = BinaryPrimitives.ReadInt32LittleEndian(c[0x24..]);
        if (count <= 0 || count > 100_000) yield break;
        var listOffset = BinaryPrimitives.ReadInt32LittleEndian(c[0x28..]);
        var offsets = new List<int>();
        var list = Cell(listOffset);
        for (var i = 0; i < count && i * 4 + 4 <= list.Length; i++)
            offsets.Add(BinaryPrimitives.ReadInt32LittleEndian(list[(i * 4)..]));
        foreach (var vk in offsets)
        {
            var v = ReadValue(vk);
            if (v is not null) yield return v.Value;
        }
    }

    private (string, object?)? ReadValue(int offset)
    {
        var c = Cell(offset);
        if (c.Length < 0x14 || c[0] != (byte)'v' || c[1] != (byte)'k') return null;
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(c[2..]);
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(c[4..]);
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(c[8..]);
        var type = BinaryPrimitives.ReadInt32LittleEndian(c[0x0C..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(c[0x10..]);
        if (0x14 + nameLen > c.Length) return null;
        var name = (flags & 1) != 0 ? Encoding.Latin1.GetString(c.Slice(0x14, nameLen)) : Encoding.Unicode.GetString(c.Slice(0x14, nameLen));

        byte[] bytes;
        var inline = (rawSize & 0x80000000) != 0;
        var size = (int)(rawSize & 0x7FFFFFFF);
        if (inline)
        {
            bytes = c.Slice(8, Math.Min(size, 4)).ToArray();
        }
        else
        {
            var d = Cell(dataOffset);
            if (size > 16344 && d.Length >= 2 && d[0] == (byte)'d' && d[1] == (byte)'b') return (name, null); // datos grandes: no hacen falta
            if (size > d.Length) return (name, null);
            bytes = d[..size].ToArray();
        }
        object? value = type switch
        {
            1 or 2 => Encoding.Unicode.GetString(bytes).TrimEnd('\0'),
            4 when bytes.Length >= 4 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            7 => Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries),
            _ => bytes,
        };
        return (name, value);
    }
}

public sealed class RegistryKeyView
{
    private readonly RegistryHive _hive;
    private readonly int _offset;

    internal RegistryKeyView(RegistryHive hive, int offset) { _hive = hive; _offset = offset; }

    public string Name => _hive.KeyName(_offset);
    public IEnumerable<RegistryKeyView> SubKeys() => _hive.SubKeyOffsets(_offset).Select(o => new RegistryKeyView(_hive, o));
    public IEnumerable<(string Name, object? Value)> Values() => _hive.Values(_offset);
    public object? Get(string name) => Values().FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
    public string? GetString(string name) => Get(name) as string;
    public int? GetInt(string name) => Get(name) as int?;
}
