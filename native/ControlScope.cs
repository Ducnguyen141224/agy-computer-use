namespace AgyComputerUse;

internal static class ControlScope
{
    // Opt-in isolation for fixture tests. Production always shares the user's stop/input scope.
    internal static readonly string Suffix = Guid.TryParseExact(Environment.GetEnvironmentVariable("AGY_DESKTOP_TEST_SCOPE"), "N", out var nonce) ? ".test." + nonce.ToString("N") : "";
}
