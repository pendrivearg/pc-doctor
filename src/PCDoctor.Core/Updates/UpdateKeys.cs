namespace PCDoctor.Core.Updates;

/// <summary>
/// Clave pública con la que cada pendrive comprueba que una versión la publicó el dueño del proyecto.
/// Va dentro del programa (no en un archivo de datos) para que nadie la pueda cambiar en el pendrive.
/// La clave privada correspondiente NO está en el código: la guarda el dueño y se usa solo para firmar versiones.
/// </summary>
public static class UpdateKeys
{
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWSli+9uAUYEMlM71yqKa/KmlSLRi
        rkJgVPsuKZrH0gHbXXayGzAMpH+ZkUXRLwVUlFmwShysEbvF0FnmFllh3Q==
        -----END PUBLIC KEY-----
        """;
}
