using System;

namespace Lagrange.OneBot.Utility;

public static class FeatureFlags
{
    // Controls runtime Realm usage. When true, features depending on Realm are disabled.
    public static bool DisableRealm { get; set; } = false;
}

