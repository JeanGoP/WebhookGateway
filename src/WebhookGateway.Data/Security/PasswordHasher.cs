using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using System.Text;

namespace WebhookGateway.Data.Security;

/// <summary>
/// Hashing de contraseñas con Argon2id, el recomendado por OWASP. Produce el formato PHC
/// que se guarda directamente en <c>AppUser.PasswordHash</c>.
/// </summary>
/// <remarks>
/// Parámetros elegidos según OWASP (2023): 19 MiB de memoria, 2 iteraciones, 1 hilo de
/// paralelismo. Es interactivo (login), no de almacenamiento masivo: el coste importa
/// porque lo paga cada intento de login, pero tiene que doler lo suficiente para que un
/// volcado de la tabla no se rompa por fuerza bruta.
/// </remarks>
public static class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MemoryKiB = 19_456;  // 19 MiB
    private const int Iterations = 2;
    private const int Parallelism = 1;

    /// <summary>Genera el hash en formato PHC: <c>$argon2id$v=19$m=19456,t=2,p=1$salt$hash</c>.</summary>
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = ComputeHash(password, salt);

        return $"$argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Verifica una contraseña contra un hash PHC. Tiempo constante en la comparación.</summary>
    public static bool Verify(string password, string phcHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(phcHash);

        var parts = phcHash.Split('$', StringSplitOptions.RemoveEmptyEntries);

        // $argon2id$v=19$m=...,t=...,p=...$salt$hash → 5 partes
        if (parts.Length != 5 || parts[0] != "argon2id")
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[3]);
        var expected = Convert.FromBase64String(parts[4]);
        var actual = ComputeHash(password, salt, ParseParameters(parts[2]));

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] ComputeHash(string password, byte[] salt, (int m, int t, int p)? parameters = null)
    {
        var (m, t, p) = parameters ?? (MemoryKiB, Iterations, Parallelism);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = m,
            Iterations = t,
            DegreeOfParallelism = p,
        };

        return argon2.GetBytes(HashBytes);
    }

    private static (int m, int t, int p) ParseParameters(string paramString)
    {
        int m = MemoryKiB, t = Iterations, p = Parallelism;

        foreach (var pair in paramString.Split(','))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            switch (kv[0])
            {
                case "m": m = int.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "t": t = int.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "p": p = int.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }

        return (m, t, p);
    }
}
