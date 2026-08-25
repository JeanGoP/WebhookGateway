using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using WebhookGateway.Core.Abstractions;

namespace WebhookGateway.Data.Security;

/// <summary>
/// Cifra la configuración de autenticación con AES-256-GCM.
/// </summary>
/// <remarks>
/// GCM es cifrado autenticado: además de ocultar el contenido, detecta manipulación. Un
/// bloque alterado falla al descifrar en vez de devolver basura.
/// <para>
/// Formato del blob: <c>[nonce 12][tag 16][texto cifrado n]</c>. La versión de clave va
/// en su propia columna, no dentro del blob.
/// </para>
/// </remarks>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;   // 96 bits: el tamaño recomendado para GCM
    private const int TagSize = 16;     // 128 bits

    private readonly IReadOnlyDictionary<int, byte[]> _keys;
    private readonly int _currentVersion;

    public AesGcmSecretProtector(IOptions<SecretProtectionOptions> options)
    {
        var value = options.Value;
        _keys = value.Decode();
        _currentVersion = value.CurrentKeyVersion;
    }

    public ProtectedSecret Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var key = _keys[_currentVersion];
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[NonceSize + TagSize + plainBytes.Length];

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);

        /*
            Nonce aleatorio por operación. Repetir un nonce con la misma clave rompe GCM
            por completo, así que nunca se deriva de nada ni se lleva un contador: 96 bits
            aleatorios dan margen de sobra para el número de secretos que maneja esto.
        */
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        CryptographicOperations.ZeroMemory(plainBytes);

        return new ProtectedSecret(blob, _currentVersion);
    }

    public string Unprotect(ProtectedSecret secret)
    {
        if (secret.IsEmpty)
        {
            return string.Empty;
        }

        if (!_keys.TryGetValue(secret.KeyVersion, out var key))
        {
            throw new CryptographicException(
                $"No hay clave para la versión {secret.KeyVersion}. " +
                "Al rotar hay que conservar las claves antiguas mientras existan secretos cifrados con ellas.");
        }

        var blob = secret.Ciphertext;

        if (blob.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("El bloque cifrado está truncado o corrupto.");
        }

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plainBytes = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagSize);

        // Lanza AuthenticationTagMismatchException si el bloque fue manipulado.
        aes.Decrypt(nonce, cipher, tag, plainBytes);

        var result = Encoding.UTF8.GetString(plainBytes);
        CryptographicOperations.ZeroMemory(plainBytes);

        return result;
    }
}
