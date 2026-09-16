namespace RoboMouse.Contracts;

/// <summary>Well-known pipe names. Kept in one place so the app, service and helper agree.</summary>
public static class PipeNames
{
    /// <summary>
    /// Control pipe the normal-user app connects to. Local only. The service ACLs it to the console
    /// user and additionally verifies the caller is the installed RoboMouse.App in the active session.
    /// </summary>
    public const string Control = "RoboMouse.Service.Control";

    /// <summary>Prefix for the per-helper pipe the service hands each helper it spawns (name + a GUID).</summary>
    public const string HelperPrefix = "RoboMouse.Helper.";

    /// <summary>Bumped when the pipe message set changes incompatibly; checked in the Hello exchange.</summary>
    public const int ProtocolVersion = 1;
}
