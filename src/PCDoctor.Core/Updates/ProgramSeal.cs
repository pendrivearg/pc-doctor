using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PCDoctor.Core.Updates;

/// <summary>
/// Sello del programa del pendrive: la huella SHA-256 de «ABRIR PC DOCTOR.exe», firmada por el dueño del proyecto.
/// Lo usa el abridor automático (PCDoctor-Abrir.exe) antes de abrir nada: un pendrive que solo se llame «PC DOCTOR»
/// o que traiga un programa cambiado no se abre solo.
/// Este archivo se compila también en el abridor (.NET Framework 4.8, que trae todo Windows 10 y 11):
/// usa solo lo que existe en los dos.
/// </summary>
public static class ProgramSeal
{
    public const string ProgramFile = "ABRIR PC DOCTOR.exe";
    public const string SealFile = "pcdoctor\\firma-programa.txt";
    public const string SignatureFile = "pcdoctor\\firma-programa.txt.firma";
    private const string Header = "PCDOCTOR-PROGRAMA 1";

    public static string SealText(string programSha256Hex) => Header + "\n" + programSha256Hex.ToUpperInvariant() + "\n";

    /// <summary>¿El programa de este pendrive es el publicado y no lo cambió nadie?</summary>
    public static bool Verify(string pendriveRoot, string publicKeyPem)
    {
        try
        {
            var seal = Path.Combine(pendriveRoot, SealFile);
            var signature = Path.Combine(pendriveRoot, SignatureFile);
            var program = Path.Combine(pendriveRoot, ProgramFile);
            if (!File.Exists(seal) || !File.Exists(signature) || !File.Exists(program)) return false;
            var sealBytes = File.ReadAllBytes(seal);
            if (sealBytes.Length > 1024) return false;
            using (var key = KeyFromPem(publicKeyPem))
                if (!key.VerifyData(sealBytes, Convert.FromBase64String(File.ReadAllText(signature).Trim()), HashAlgorithmName.SHA256)) return false;
            var lines = Encoding.UTF8.GetString(sealBytes).Split('\n');
            if (lines.Length < 2 || lines[0] != Header) return false;
            return string.Equals(lines[1].Trim(), Sha256Hex(program), StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static string Sha256Hex(string path)
    {
        using (var sha = SHA256.Create())
        using (var s = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
    }

    /// <summary>Clave pública P-256 en formato PEM (SubjectPublicKeyInfo: 26 bytes fijos + 04 + X + Y).</summary>
    private static ECDsa KeyFromPem(string pem)
    {
        var body = new StringBuilder();
        foreach (var line in pem.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)) body.Append(l);
        }
        var der = Convert.FromBase64String(body.ToString());
        if (der.Length != 91 || der[26] != 0x04) throw new CryptographicException("La clave pública no es P-256.");
        var x = new byte[32];
        var y = new byte[32];
        Array.Copy(der, 27, x, 0, 32);
        Array.Copy(der, 59, y, 0, 32);
        return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
    }
}
