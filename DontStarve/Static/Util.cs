using System;
using Microsoft.Xna.Framework;

namespace DontStarve;

internal static class Util
{
    internal static float Distance(Vector2 a, Vector2 b)
    {
        return MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2));
    }
}
