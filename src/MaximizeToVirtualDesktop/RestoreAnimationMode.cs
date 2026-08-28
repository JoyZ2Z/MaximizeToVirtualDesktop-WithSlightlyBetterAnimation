namespace MaximizeToVirtualDesktop;

/// <summary>Where Windows should play a tracked window's exit animation.</summary>
internal enum RestoreAnimationMode
{
    /// <summary>Finish drag/minimize on the managed desktop, then return.</summary>
    ManagedDesktop = 0,

    /// <summary>Return to Desktop 1 first, then let Windows play the animation.</summary>
    DesktopOne = 1,
}
