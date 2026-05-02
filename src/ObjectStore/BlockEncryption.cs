using System.Security.Cryptography;

namespace ObjectStore;

/// <summary>
/// AES-256-GCM per-block encryption with random IVs.
/// Each encrypted block: [12-byte nonce][16-byte auth tag][ciphertext]
/// </summary>
public sealed class BlockEncryption
{
    private readonly byte[] _key; // 32 bytes (256 bits)
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const int Overhead = NonceSize + TagSize; // 28 bytes

    public BlockEncryption(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Encryption key must be exactly 32 bytes (256 bits).");
        _key = (byte[])key.Clone();
    }

    /// <summary>Creates a BlockEncryption from a passphrase using HKDF-SHA256.</summary>
    public static BlockEncryption FromPassphrase(string passphrase, byte[]? salt = null)
    {
        salt ??= new byte[32]; // Default salt (should be per-container in production)
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256,
            System.Text.Encoding.UTF8.GetBytes(passphrase), salt);
        byte[] key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32,
            "ObjectStore-BlockEncryption-v1"u8.ToArray());
        return new BlockEncryption(key);
    }

    /// <summary>Creates a BlockEncryption from a raw 32-byte key.</summary>
    public static BlockEncryption FromKey(byte[] key) => new(key);

    /// <summary>Encrypts plaintext. Returns [nonce][tag][ciphertext].</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        byte[] result = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = result.AsSpan(0, NonceSize);
        var tag = result.AsSpan(NonceSize, TagSize);
        var ciphertext = result.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return result;
    }

    /// <summary>Decrypts data. Input format: [nonce][tag][ciphertext].</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length < Overhead)
            throw new CryptographicException("Encrypted data too short.");

        var nonce = encrypted[..NonceSize];
        var tag = encrypted[NonceSize..(NonceSize + TagSize)];
        var ciphertext = encrypted[(NonceSize + TagSize)..];

        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}
