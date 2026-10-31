using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Mintokei.Memory;

public record CompletedMemoryTurn(string SourceSystem, Guid TaskId, Guid TurnId, string Title, Guid? WorkspaceId, string Status, IReadOnlyList<MemoryMessage> Messages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] MemorySearchScope SearchScope = MemorySearchScope.AllAuthorizedHistory);
