using System.Security.Cryptography;

namespace UmbraSync.Services.Housing;

/// <summary>
/// Dérivation de clé commune aux partages chiffrés (housing furniture et scénarios NPC).
/// ATTENTION : toute évolution doit rester rétrocompatible — la même dérivation sert à
/// déchiffrer les partages déjà publiés côté serveur.
/// </summary>
public static class ShareCryptoHelper
{
    public static byte[] DeriveKey(Guid shareId, byte[] salt)
    {
        byte[] shareBytes = shareId.ToByteArray();
        byte[] material = new byte[shareBytes.Length + salt.Length];
        Buffer.BlockCopy(shareBytes, 0, material, 0, shareBytes.Length);
        Buffer.BlockCopy(salt, 0, material, shareBytes.Length, salt.Length);
        return SHA256.HashData(material);
    }
}
