using System.Runtime.Versioning;

// The core talks to Win32 directly; it is Windows-only even though it targets the portable TFM so it
// builds and unit-tests on any OS.
[assembly: SupportedOSPlatform("windows")]
