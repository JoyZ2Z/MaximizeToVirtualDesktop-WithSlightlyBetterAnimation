namespace MaximizeToVirtualDesktop;

/// <summary>Builds a stable desktop order without performing any COM operations.</summary>
internal static class DesktopOrderPolicy
{
    public static IReadOnlyList<Guid> CreateTargetOrder(
        Guid mainDesktopId,
        IReadOnlyList<Guid> mostRecentlyUsed,
        IReadOnlyList<Guid> currentOrder)
    {
        var result = new List<Guid> { mainDesktopId };

        foreach (var desktopId in mostRecentlyUsed)
        {
            if (desktopId != mainDesktopId && currentOrder.Contains(desktopId) && !result.Contains(desktopId))
                result.Add(desktopId);
        }

        foreach (var desktopId in currentOrder)
        {
            if (!result.Contains(desktopId)) result.Add(desktopId);
        }

        return result;
    }
}
