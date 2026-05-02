using System.Security.Cryptography;

namespace ObjectStore.Tests;

public class EncryptionTests
{
    private static byte[] TestKey => new byte[32] {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32
    };

    [Fact]
    public void RoundTrip_SmallData()
    {
        var enc = BlockEncryption.FromKey(TestKey);
        byte[] plaintext = "Hello, encrypted world!"u8.ToArray();

        byte[] encrypted = enc.Encrypt(plaintext);
        Assert.True(encrypted.Length > plaintext.Length); // Has overhead
        Assert.Equal(plaintext.Length + BlockEncryption.Overhead, encrypted.Length);

        byte[] decrypted = enc.Decrypt(encrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_LargeData()
    {
        var enc = BlockEncryption.FromKey(TestKey);
        byte[] plaintext = new byte[4096];
        Random.Shared.NextBytes(plaintext);

        byte[] encrypted = enc.Encrypt(plaintext);
        byte[] decrypted = enc.Decrypt(encrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DifferentNonce_EachEncrypt()
    {
        var enc = BlockEncryption.FromKey(TestKey);
        byte[] plaintext = "same data"u8.ToArray();

        byte[] enc1 = enc.Encrypt(plaintext);
        byte[] enc2 = enc.Encrypt(plaintext);

        // Nonces should differ (first 12 bytes)
        Assert.False(enc1.AsSpan(0, 12).SequenceEqual(enc2.AsSpan(0, 12)));
    }

    [Fact]
    public void Tampered_Ciphertext_ThrowsCryptographicException()
    {
        var enc = BlockEncryption.FromKey(TestKey);
        byte[] plaintext = "sensitive data"u8.ToArray();

        byte[] encrypted = enc.Encrypt(plaintext);
        // Flip a bit in the ciphertext
        encrypted[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => enc.Decrypt(encrypted));
    }

    [Fact]
    public void Tampered_Tag_ThrowsCryptographicException()
    {
        var enc = BlockEncryption.FromKey(TestKey);
        byte[] plaintext = "sensitive data"u8.ToArray();

        byte[] encrypted = enc.Encrypt(plaintext);
        // Flip a bit in the auth tag (bytes 12..28)
        encrypted[15] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => enc.Decrypt(encrypted));
    }

    [Fact]
    public void FromPassphrase_Deterministic()
    {
        byte[] salt = new byte[32];
        salt[0] = 42;

        var enc1 = BlockEncryption.FromPassphrase("my secret", salt);
        var enc2 = BlockEncryption.FromPassphrase("my secret", salt);

        byte[] plaintext = "test"u8.ToArray();
        // Both should be able to decrypt each other's output
        byte[] encrypted = enc1.Encrypt(plaintext);
        byte[] decrypted = enc2.Decrypt(encrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void WrongKey_Fails()
    {
        var enc1 = BlockEncryption.FromKey(TestKey);
        byte[] wrongKey = new byte[32];
        wrongKey[0] = 99;
        var enc2 = BlockEncryption.FromKey(wrongKey);

        byte[] plaintext = "secret"u8.ToArray();
        byte[] encrypted = enc1.Encrypt(plaintext);

        Assert.ThrowsAny<CryptographicException>(() => enc2.Decrypt(encrypted));
    }

    [Fact]
    public void InvalidKeyLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => BlockEncryption.FromKey(new byte[16]));
    }
}
