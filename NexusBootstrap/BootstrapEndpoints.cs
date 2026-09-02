namespace NexusBootstrap;

// One obvious place to find/change Console's base URL (2026-09-01, Phase 5 recheck) - previously a
// required --consoleBaseUrl CLI argument, but Bootstrap is the one making the actual Console calls
// (not its caller), and the URL is a fixed, non-per-machine value - exactly like NexusHttpClient.cs's
// own hardcoded copy of this same URL on the iYamPOS side. No configurable override exists today; if
// one's ever added, it must be validated as HTTPS-only before use.
internal static class BootstrapEndpoints
{
    public const string ConsoleBaseUrl = "https://nexus-portalapi.azurewebsites.net";
}