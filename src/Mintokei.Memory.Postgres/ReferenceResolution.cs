using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Postgres;

public class ReferenceResolutionService(IDbContextFactory<MemoryDbContext> factory, HistorySearch history, Reasoner reasoner, TraceRecorder traces, IMemoryEvents bus) : IReferenceResolutionService
{
    public async Task<List<ResolvedReference>> ResolveAsync(Conversation c, Message message, IReadOnlyList<Message> recent, JsonNode state, MemoryConfiguration config, Guid trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cached = await db.Resolutions.Where(x => x.ConversationId == c.Id && x.SourceMessageId == message.Id && x.ThroughSequence == message.SequenceNumber).ToListAsync(ct);
        if (cached.Count > 0)
        {
            var latest = RetrievalPolicy.Latest(cached.Select(x => Json.Read<ResolvedReference>(x.ResultJson)));
            var ids = latest.SelectMany(r => r.Evidence.Select(e => e.MessageId)).Distinct().ToArray();
            var local = RetrievalPolicy.IsExplicitlyLocal(message.Content);
            var evidence = await (
                from m in db.Messages
                join conv in db.Conversations on m.ConversationId equals conv.Id
                where ids.Contains(m.Id) && conv.UserId == c.UserId && conv.Namespace == c.Namespace && (!local || conv.Id == c.Id) && m.CreatedAt <= message.CreatedAt && m.SequenceNumber <= conv.VisibleSequence && (conv.Id != c.Id || m.SequenceNumber <= message.SequenceNumber)select m).ToDictionaryAsync(m => m.Id, ct);
            if (latest.All(r => r.Status != "resolved" && r.Evidence.Count == 0 || EvidenceValidator.Validate(r.Evidence, evidence).Count == 0 && (r.Status != "resolved" || r.Evidence.Any(e => e.MessageId != message.Id))))
                return latest;
            var rejected = await traces.Start(c, message.TurnId, trace, "reference-cache-validation", new { sourceMessageId = message.Id, cachedCount = latest.Count }, config.CapturePayloads);
            await traces.Finish(rejected, new { reused = false, reason = "Cached evidence is missing, outside the authorized/requested scope, or no longer validates; resolve again." }, config.CapturePayloads);
        }

        if (!config.Operations["reference-detection"].Enabled)
            return[];
        if (System.Text.RegularExpressions.Regex.IsMatch(message.Content, @"^I (have|am|prefer|expect|work)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(message.Content, @"\b(that|those|previous|earlier|first|second|same|before|again|original|discussed|latest)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var fast = await traces.Start(c, message.TurnId, trace, "reference-detection-fast-path", new { message.Id, message.Content }, config.CapturePayloads);
            await traces.Finish(fast, new { references = Array.Empty<object>(), reason = "Explicit self-contained statement without historical reference markers" }, config.CapturePayloads);
            return[];
        }

        var detection = await reasoner.Run(c, message.TurnId, trace, "reference-detection", new { newMessage = message, recentMessages = recent, conversationState = state, allowedSearchScope = RetrievalPolicy.IsExplicitlyLocal(message.Content) ? "current_conversation" : "authorized_user_history" }, config, ct);
        var refs = Json.Read<List<ReferenceRequest>>(Json.Node(detection.Output!)!["references"]!.ToJsonString());
        return await ResolveRequestsAsync(c, message, recent, state, refs, config, trace, ct);
    }

    public async Task<List<ResolvedReference>> ResolveRequestsAsync(Conversation c, Message message, IReadOnlyList<Message> recent, JsonNode state, IEnumerable<ReferenceRequest> references, MemoryConfiguration config, Guid trace, CancellationToken ct)
    {
        var results = new List<ResolvedReference>();
        foreach (var proposed in references.Take(4))
        {
            if (proposed.SourceMessageId != message.Id || string.IsNullOrWhiteSpace(proposed.Expression) || !message.Content.Contains(proposed.Expression, StringComparison.Ordinal))
                continue;
            var local = RetrievalPolicy.IsExplicitlyLocal(message.Content) || proposed.Scope == "current_conversation";
            var reference = proposed with
            {
                Scope = local ? "current_conversation" : "cross_conversation"
            };
            var scope = new SearchScope(c.UserId, c.Namespace, c.Id, message.SequenceNumber, message.CreatedAt, local, message.Id);
            var span = await traces.Start(c, message.TurnId, trace, "historical-search", new { reference, scope }, config.CapturePayloads);
            var attempts = new List<SearchAttempt>();
            IReadOnlyList<SearchCandidate> candidates = [];
            try
            {
                if (config.MaxSearchRounds > 0)
                    candidates = await history.SearchAsync(scope, reference.Query, config.TopK * 2, reference.TemporalHint, ct);
                attempts.Add(new("messages", local ? "current_conversation" : "authorized_user_history", config.MaxSearchRounds > 0 ? "completed" : "skipped", reference.Query, config.MaxSearchRounds > 0 ? candidates.Count : null, span.Id));
                var adjacent = await history.ExpandAsync(scope, candidates, ct);
                await traces.Finish(span, new { candidates, adjacent, searchAttempts = attempts }, config.CapturePayloads);
                results.Add(await ResolveEvidence(c, message, recent, state, reference, candidates, adjacent, attempts, config, trace, span.Id, ct));
            }
            catch (Exception ex)
            {
                if (span.Status == "running")
                    await traces.Finish(span, new { searchAttempts = new[] { new SearchAttempt("messages", local ? "current_conversation" : "authorized_user_history", "failed", reference.Query, null, span.Id, ex.Message) } }, config.CapturePayloads, ex.Message);
                throw;
            }
        }

        return results;
    }

    public async Task<List<ResolvedReference>> RecheckAsync(Conversation c, Message message, IReadOnlyList<Message> recent, JsonNode state, List<ResolvedReference> references, IReadOnlyList<Message> evidence, List<SearchAttempt> attempts, MemoryConfiguration config, Guid traceId, CancellationToken ct)
    {
        // Re-authorize supplied IDs, including replay/time boundaries. Do not trust a caller's message objects.
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = evidence.Select(m => m.Id).ToArray();
        var local = RetrievalPolicy.IsExplicitlyLocal(message.Content);
        var allowed = await (
            from m in db.Messages
            join conv in db.Conversations on m.ConversationId equals conv.Id
            where ids.Contains(m.Id) && m.Id != message.Id && conv.UserId == c.UserId && conv.Namespace == c.Namespace && (!local || conv.Id == c.Id) && m.CreatedAt <= message.CreatedAt && m.SequenceNumber <= conv.VisibleSequence && (conv.Id != c.Id || m.SequenceNumber < message.SequenceNumber)select m).ToListAsync(ct);
        if (allowed.Count == 0 || config.MaxSearchRounds <= 0)
            return references;
        var candidates = allowed.Select(m => new SearchCandidate(m.Id, "message", m.ConversationId, m.SequenceNumber, m.CreatedAt, m.Content, 0, "source_evidence", Json.Write(new { role = m.Role, notRanked = true }))).ToList();
        var results = new List<ResolvedReference>();
        foreach (var resolution in references)
        {
            if (resolution.Status == "resolved")
            {
                results.Add(resolution);
                continue;
            }

            var request = (resolution.SearchRequest ?? new(resolution.ReferenceId, message.Id, resolution.Expression, resolution.Expression, "cross_conversation", "none"))with
            {
                Scope = local ? "current_conversation" : "cross_conversation"
            };
            var span = await traces.Start(c, message.TurnId, traceId, "reference-recheck", new { previous = resolution, evidenceIds = allowed.Select(m => m.Id), searchAttempts = attempts }, config.CapturePayloads);
            try
            {
                var updated = await ResolveEvidence(c, message, recent, state, request, candidates, allowed, attempts, config, traceId, span.Id, ct);
                await traces.Finish(span, updated, config.CapturePayloads);
                results.Add(updated);
            }
            catch (Exception ex)
            {
                await traces.Finish(span, null, config.CapturePayloads, ex.Message);
                throw;
            }
        }

        return results;
    }

    private async Task<ResolvedReference> ResolveEvidence(Conversation c, Message message, IReadOnlyList<Message> recent, JsonNode state, ReferenceRequest reference, IReadOnlyList<SearchCandidate> candidates, IReadOnlyList<Message> adjacent, List<SearchAttempt> attempts, MemoryConfiguration config, Guid trace, Guid parent, CancellationToken ct)
    {
        var allowed = recent.Concat(adjacent).Append(message).DistinctBy(x => x.Id).ToDictionary(x => x.Id);
        var coverage = new RetrievalCoverage(attempts, RetrievalPolicy.IsExplicitlyLocal(message.Content));
        var execution = await reasoner.Run(c, message.TurnId, trace, "reference-resolution", new { originalMessage = message, reference, recentMessages = recent, conversationState = state, candidates, adjacent, searchCoverage = coverage }, config, ct, parent: parent);
        var resolution = Json.Read<ResolvedReference>(execution.Output!);
        return (await ValidateProposalsAsync(c, message, [reference], [resolution], allowed.Values.ToList(), candidates, attempts, config, trace, parent, ct)).Single();
    }

    public async Task<List<ResolvedReference>> ValidateProposalsAsync(Conversation c, Message message, IEnumerable<ReferenceRequest> requests, IEnumerable<ResolvedReference> proposals, IReadOnlyList<Message> suppliedEvidence, IReadOnlyList<SearchCandidate> candidates, List<SearchAttempt> attempts, MemoryConfiguration config, Guid trace, Guid parent, CancellationToken ct)
    {
        // Authorize originals again; model IDs and caller-provided message bodies are not trusted.
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = suppliedEvidence.Select(m => m.Id).Append(message.Id).Distinct().ToArray();
        var local = RetrievalPolicy.IsExplicitlyLocal(message.Content);
        var originals = await (
            from m in db.Messages
            join conv in db.Conversations on m.ConversationId equals conv.Id
            where ids.Contains(m.Id) && conv.UserId == c.UserId && conv.Namespace == c.Namespace && (!local || conv.Id == c.Id) && m.CreatedAt <= message.CreatedAt && m.SequenceNumber <= conv.VisibleSequence && (conv.Id != c.Id || m.SequenceNumber <= message.SequenceNumber)select m).ToListAsync(ct);
        var proposed = proposals.ToList();
        var results = new List<ResolvedReference>();
        foreach (var reference in requests.DistinctBy(r => r.ReferenceId).Take(4))
        {
            if (reference.SourceMessageId != message.Id || string.IsNullOrWhiteSpace(reference.Expression) || !message.Content.Contains(reference.Expression, StringComparison.Ordinal))
                continue;
            var allowed = originals.Where(m => reference.Scope != "current_conversation" || m.ConversationId == c.Id).ToDictionary(m => m.Id);
            var matches = proposed.Where(r => r.ReferenceId == reference.ReferenceId).ToList();
            var resolution = matches.Count == 1 ? matches[0] : new ResolvedReference(reference.ReferenceId, message.Id, reference.Expression, "not_found", null, [], "No unique evidence-based interpretation supplied");
            var validation = ValidateEvidence(reference, resolution, message, allowed, candidates);
            if (validation.Count > 0)
                resolution = new(reference.ReferenceId, message.Id, reference.Expression, "not_found", null, [], "Evidence validation failed: " + string.Join("; ", validation));
            if (resolution.Status != "resolved")
                resolution = resolution with
                {
                    ResolvedValue = null
                };
            resolution = resolution with
            {
                SearchRequest = reference,
                SearchAttempts = attempts.ToList(),
                EvaluatedAt = DateTimeOffset.UtcNow
            };
            db.Resolutions.Add(new ResolutionRecord { UserId = c.UserId, Namespace = c.Namespace, ConversationId = c.Id, TurnId = message.TurnId, SourceMessageId = message.Id, ThroughSequence = message.SequenceNumber, Expression = reference.Expression, Status = resolution.Status, ResultJson = Json.Write(resolution), TraceId = trace });
            await db.SaveChangesAsync(ct);
            var check = await traces.Start(c, message.TurnId, trace, "reference-evidence-validation", new { reference, validation }, config.CapturePayloads, parent);
            await traces.Finish(check, resolution, config.CapturePayloads);
            await bus.Send(c.Id, "reference.resolved", resolution, ct);
            results.Add(resolution);
        }

        return results;
    }

    public static List<string> ValidateEvidence(ReferenceRequest reference, ResolvedReference resolution, Message message, IReadOnlyDictionary<Guid, Message> allowed, IReadOnlyList<SearchCandidate> candidates)
    {
        var validation = resolution.Status == "resolved" || resolution.Evidence.Count > 0 ? EvidenceValidator.Validate(resolution.Evidence, allowed) : new List<string>();
        if (resolution.SourceMessageId != message.Id || resolution.Expression != reference.Expression)
            validation.Add("Resolver changed reference identity");
        if (resolution.Status is not ("resolved" or "ambiguous" or "not_found"))
            validation.Add("Invalid resolution status");
        if (resolution.Status == "resolved")
        {
            if (resolution.ResolvedValue == null)
                validation.Add("Missing resolved value");
            if (!resolution.Evidence.Any(x => x.MessageId != message.Id))
                validation.Add("No historical evidence");
            if (!resolution.Evidence.Any(x => x.MessageId == message.Id))
                validation.Add("Missing current instruction evidence");
            if (reference.TemporalHint is "first" or "original" or "beginning")
            {
                var candidateIds = candidates.Where(x => x.SourceType == "message").Select(x => x.SourceId).ToHashSet();
                var first = allowed.Values.Where(m => m.Id != message.Id && m.Role == "user" && candidateIds.Contains(m.Id)).OrderBy(m => m.CreatedAt).ThenBy(m => m.SequenceNumber).FirstOrDefault();
                if (first != null && !resolution.Evidence.Any(x => x.MessageId == first.Id))
                    validation.Add("Original/first constraint not supported by earliest retrieved user evidence");
            }
        }

        return validation;
    }
}
