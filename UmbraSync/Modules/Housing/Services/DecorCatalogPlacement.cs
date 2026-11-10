using System.Numerics;

namespace UmbraSync.Modules.Housing.Services;

public static class DecorCatalogPlacement
{
    public static Vector3 Translate(float[]? authorTranslation, Vector3 anchor, Vector3 player)
    {
        Vector3 author = authorTranslation is { Length: 3 } t ? new Vector3(t[0], t[1], t[2]) : Vector3.Zero;
        return author + (player - anchor);
    }
}
