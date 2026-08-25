namespace WebhookGateway.Core.Abstractions;

/// <summary>
/// Bloque cifrado tal como se guarda. <paramref name="KeyVersion"/> permite rotar la clave
/// maestra sin tener que descifrar y volver a cifrar todo de golpe.
/// </summary>
public readonly record struct ProtectedSecret(byte[] Ciphertext, int KeyVersion)
{
    public static readonly ProtectedSecret Empty = new([], 0);

    public bool IsEmpty => Ciphertext.Length == 0;
}

/// <summary>
/// Cifra y descifra la configuración de autenticación de los endpoints.
/// Se cifra el JSON entero, no campo por campo: nada de ahí dentro se consulta nunca,
/// así que no hay razón para dejar partes en claro ni riesgo de olvidar un campo nuevo.
/// </summary>
public interface ISecretProtector
{
    ProtectedSecret Protect(string plaintext);

    /// <summary>Descifra. Lanza si el bloque fue manipulado o la versión de clave no existe.</summary>
    string Unprotect(ProtectedSecret secret);
}
