using System.Net;
using System.Text;
using J = Mintokei.Memory.Json;
namespace Mintokei.Memory.Testing;
internal static class ScriptedResponses
{
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return run(request, ct);
        }
    }

    internal static string Frame(object e) => "event: ignored\r\ndata: " + J.Write(e) + "\r\n\r\n";
    internal static HttpResponseMessage StreamResponse(string data) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(data, Encoding.UTF8, "text/event-stream")
    };
    internal static object Completed(string text, string status = "completed", object? usage = null) => new
    {
        type = "response." + status,
        response = new
        {
            id = "resp_protocol_test",
            status,
            model = "gpt-6-luna-test-snapshot",
            reasoning = new
            {
                effort = "low"
            },
            usage,
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text,
                            annotations = Array.Empty<object>()
                        }
                    }
                }
            }
        }
    };
}
