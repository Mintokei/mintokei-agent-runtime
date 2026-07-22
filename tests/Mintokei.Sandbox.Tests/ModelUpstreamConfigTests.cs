using Mintokei.Sandbox.Broker;
using Xunit;

namespace Mintokei.Sandbox.Tests;

public class ModelUpstreamConfigTests
{
    [Fact]
    public void Parse_reads_the_legacy_single_upstream_with_default_port()
    {
        var entries = ModelUpstreamConfig.Parse(new Dictionary<string, string?>
        {
            ["BROKER_MODEL_UPSTREAM"] = "https://api.anthropic.com",
            ["BROKER_MODEL_AUTH"] = "x-api-key=sk",
        });

        var e = Assert.Single(entries);
        Assert.Equal("default", e.Name);
        Assert.Equal(3130, e.Port);                                        // default legacy port
        Assert.Equal("https://api.anthropic.com", e.Upstream);
        Assert.Equal(new KeyValuePair<string, string>("x-api-key", "sk"), Assert.Single(e.Headers));
    }

    [Fact]
    public void Parse_reads_named_providers_each_on_its_own_port()
    {
        var entries = ModelUpstreamConfig.Parse(new Dictionary<string, string?>
        {
            ["BROKER_MODEL_ANTHROPIC_UPSTREAM"] = "https://api.anthropic.com",
            ["BROKER_MODEL_ANTHROPIC_PORT"] = "3130",
            ["BROKER_MODEL_ANTHROPIC_AUTH"] = "Authorization: Bearer ant",
            ["BROKER_MODEL_OPENAI_UPSTREAM"] = "https://api.openai.com",
            ["BROKER_MODEL_OPENAI_PORT"] = "3131",
            ["BROKER_MODEL_OPENAI_AUTH"] = "Authorization: Bearer oai",
        });

        Assert.Equal(2, entries.Count);
        var a = entries.Single(x => x.Name == "anthropic");
        Assert.Equal(3130, a.Port);
        Assert.Equal("https://api.anthropic.com", a.Upstream);
        Assert.Equal("Bearer ant", a.Headers.Single(h => h.Key == "Authorization").Value);
        var o = entries.Single(x => x.Name == "openai");
        Assert.Equal(3131, o.Port);                                        // distinct port
        Assert.Equal("https://api.openai.com", o.Upstream);
    }

    [Fact]
    public void Parse_skips_a_named_provider_without_a_port()
    {
        var entries = ModelUpstreamConfig.Parse(new Dictionary<string, string?>
        {
            ["BROKER_MODEL_OPENAI_UPSTREAM"] = "https://api.openai.com", // no _PORT → skipped (runtime always sets it)
        });
        Assert.Empty(entries);
    }

    [Fact]
    public void Parse_ignores_non_model_env_and_returns_empty()
    {
        Assert.Empty(ModelUpstreamConfig.Parse(new Dictionary<string, string?>
        {
            ["BROKER_ALLOW"] = "github.com",
            ["BROKER_PORT"] = "3128",
        }));
    }
}
