namespace DontStarve.Display;

internal interface IHudStatusBarProvider
{
    string HudBarId { get; }
    bool TryGetLayoutRequest(out HudBarLayoutRequest request);
}
