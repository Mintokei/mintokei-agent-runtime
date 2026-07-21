using System.Text;

namespace Mintokei.Runner.Contracts;

/// <summary>
/// Encodes an argv list into the single command-line string the RunCommand RPC carries.
///
/// The runner executes remote commands via <c>ProcessStartInfo { Arguments = &lt;string&gt;,
/// UseShellExecute = false }</c> — a single string, no shell. .NET parses that string back into argv using
/// its cross-platform rules, so each token must be quoted/escaped exactly the way .NET's own
/// <c>ArgumentList → Arguments</c> serializer (PasteArguments) does — otherwise args that contain spaces
/// (mount paths, <c>--format</c> templates, env values, repo URLs) split incorrectly. This is a faithful
/// port of that algorithm; round-tripping is covered by unit tests.
/// </summary>
public static class RunCommandArgs
{
    public static string Encode(IReadOnlyList<string> argv)
    {
        var sb = new StringBuilder();
        foreach (var arg in argv)
            AppendArgument(sb, arg);
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (sb.Length != 0)
            sb.Append(' ');

        // No quoting needed for a non-empty token free of whitespace and quotes.
        if (argument.Length != 0 && ContainsNoWhitespaceOrQuotes(argument))
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        var idx = 0;
        while (idx < argument.Length)
        {
            var c = argument[idx++];
            if (c == '\\')
            {
                var backslashes = 1;
                while (idx < argument.Length && argument[idx] == '\\')
                {
                    idx++;
                    backslashes++;
                }

                if (idx == argument.Length)
                    sb.Append('\\', backslashes * 2);          // escape trailing backslashes before the closing quote
                else if (argument[idx] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);      // escape the backslashes AND the quote
                    sb.Append('"');
                    idx++;
                }
                else
                    sb.Append('\\', backslashes);              // backslashes not before a quote are literal
            }
            else if (c == '"')
            {
                sb.Append('\\').Append('"');
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append('"');
    }

    private static bool ContainsNoWhitespaceOrQuotes(string s)
    {
        foreach (var c in s)
            if (char.IsWhiteSpace(c) || c == '"')
                return false;
        return true;
    }
}
