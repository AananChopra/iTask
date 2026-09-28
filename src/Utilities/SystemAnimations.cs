using System.Windows;

namespace iTask.Utilities;

/// <summary>Whether to animate at all: follows Windows' "Animation effects" (Accessibility > Visual effects).</summary>
public static class SystemAnimations
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;
}
