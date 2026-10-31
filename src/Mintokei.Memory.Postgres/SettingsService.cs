using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Postgres;

public class SettingsService(IDbContextFactory<MemoryDbContext> factory)
{
    public async Task<MemoryConfiguration> Get(Conversation? c = null, CancellationToken ct = default)
    {
        if (c?.ConfigurationJson is not null)
        {
            var frozen = Json.Read<MemoryConfiguration>(c.ConfigurationJson).WithContextOperations();
            if (Json.Node(c.ConfigurationJson)?["contextMode"] == null)
                frozen.ContextMode = "separated";
            return frozen;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        return Json.Read<MemoryConfiguration>((await db.Settings.SingleAsync(x => x.Key == "default", ct)).ValueJson).WithContextOperations();
    }
}
