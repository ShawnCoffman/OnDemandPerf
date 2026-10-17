namespace OnDemandPerf.Core;

/// <summary>Fits the tray panel to a monitor's working area. Pure arithmetic in device pixels, so it is tested without a screen.</summary>
public static class PanelFit
{
    /// <param name="preferred">The panel's designed height.</param>
    /// <param name="minimum">The shortest height at which the fixed rows do not overlap.</param>
    /// <param name="available">The working-area height left after margins.</param>
    /// <returns>
    /// The window height, never taller than <paramref name="available"/>, and the content height to scroll through: 0 when the
    /// content fits, otherwise <paramref name="minimum"/> (a very short or high-DPI screen keeps every control reachable).
    /// </returns>
    public static (int Height, int ScrollHeight) Compute(int preferred, int minimum, int available)
    {
        int height = Math.Max(1, Math.Min(preferred, available));
        return (height, height < minimum ? minimum : 0);
    }
}
