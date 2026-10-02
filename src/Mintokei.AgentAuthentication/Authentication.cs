namespace Mintokei.AgentAuthentication;

/// <summary>Trusted official CLI executable, supplied by the embedding application.</summary>
public sealed record CodexAccountOptions(string Executable, string ClientName = "mintokei_account_manager");

/// <summary>Sanitized authentication failure. Applications decide how to persist reconnect state.</summary>
public sealed class AgentAuthenticationException(string message) : Exception(message);

internal static class AuthenticationGuard
{
    public static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new AgentAuthenticationException(message);
    }
}
