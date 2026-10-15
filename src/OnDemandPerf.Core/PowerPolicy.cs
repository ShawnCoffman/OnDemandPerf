namespace OnDemandPerf.Core;

/// <summary>
/// Names the Windows power plan and power mode from their GUIDs (names are localized and editable, GUIDs are not) and reads the
/// plan's processor limits. A run in a power-saving plan or mode is invalid; see <see cref="FindingRules"/>.
/// </summary>
public static class PowerPolicy
{
    internal static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    internal static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    internal static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    internal static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    // Settings > System > Power > Power mode overlays. The empty GUID is the plan's own (balanced) behaviour.
    internal static readonly Guid BestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    internal static readonly Guid BatterySaverMode = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    internal static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    public static string PlanType(Guid plan) =>
        plan == Balanced ? "Balanced" : plan == HighPerformance ? "HighPerformance"
        : plan == UltimatePerformance ? "UltimatePerformance" : plan == PowerSaver ? "PowerSaver" : "Custom";

    public static string PowerMode(Guid overlay) =>
        overlay == Guid.Empty || overlay == Balanced ? "Balanced" : overlay == BestPerformance ? "BestPerformance"
        : overlay == BestPowerEfficiency ? "BestPowerEfficiency" : overlay == BatterySaverMode ? "BatterySaver"
        : "Other " + overlay;

    /// <summary>The power mode in effect, or null where Windows does not expose it.</summary>
    public static string? ReadPowerMode()
    {
        try { return WindowsNative.PowerGetEffectiveOverlayScheme(out var overlay) == 0 ? PowerMode(overlay) : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>(maximum processor state percent, boost mode) of a plan for AC or battery; each null when unreadable.</summary>
    public static (uint? MaxState, uint? Boost) ReadProcessorLimits(Guid plan, bool onBattery) =>
        (ReadSetting(plan, MaxProcessorState, onBattery), ReadSetting(plan, BoostMode, onBattery));

    private static uint? ReadSetting(Guid plan, Guid setting, bool onBattery)
    {
        var subgroup = ProcessorSubgroup;
        uint error = onBattery
            ? WindowsNative.PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, out uint value)
            : WindowsNative.PowerReadACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, out value);
        return error == 0 ? value : null;
    }
}
