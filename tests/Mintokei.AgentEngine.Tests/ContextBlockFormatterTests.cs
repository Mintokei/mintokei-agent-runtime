namespace Mintokei.AgentEngine.Tests;

/// <summary>
/// Tests <see cref="ContextBlockFormatter"/> — the engine helper that prepends a workspace context block
/// to a user message. (Relocated from the product WorkspaceContextFileReader tests: it exercises an engine
/// type, not the product reader, so it lives beside the engine.)
/// </summary>
public class ContextBlockFormatterTests
{
    [Fact]
    public void FormatMessageWithContext_WrapsUserMessage()
    {
        var contextBlock = "<context-files>\n<file path=\"a.txt\">\nhello\n</file>\n</context-files>\n";
        var userMessage = "Do the thing";

        var result = ContextBlockFormatter.FormatMessageWithContext(contextBlock, userMessage);

        Assert.StartsWith(contextBlock, result);
        Assert.Contains("<user-message>", result);
        Assert.Contains("Do the thing", result);
        Assert.Contains("</user-message>", result);
    }
}
