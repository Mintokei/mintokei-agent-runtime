using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Mintokei.Memory.Postgres;

public class MemoryPipeline(IDbContextFactory<MemoryDbContext> factory, SettingsService settings, HistorySearch history, ReferenceResolutionService resolver, Reasoner reasoner, TraceRecorder traces, IMemoryEvents bus, PostgresMemoryStore postgres, Mem0MemoryStore mem0)
{
    public async Task Process(Job job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var c = await db.Conversations.SingleAsync(x => x.Id == job.ConversationId, ct);
        if (c.LastProcessedSequence >= job.ThroughSequence)
            return;
        var config = await settings.Get(c, ct);
        var recordedScope = Json.Node(job.PayloadJson)?["searchScope"]?.Deserialize<MemorySearchScope>(Json.Options)
            ?? MemorySearchScope.AllAuthorizedHistory;
        config.SearchScope = RetrievalPolicy.RestrictScope(recordedScope, config.SearchScope);
        var trace = Guid.NewGuid();
        var root = await traces.Start(c, job.TurnId, trace, "memory-pipeline", new { checkpoint = c.LastProcessedSequence, job.ThroughSequence, job.Attempts }, config.CapturePayloads);
        try
        {
            var fresh = await db.Messages.Where(x => x.ConversationId == c.Id && x.SequenceNumber > c.LastProcessedSequence && x.SequenceNumber <= job.ThroughSequence && x.SequenceNumber <= c.VisibleSequence).OrderBy(x => x.SequenceNumber).ToListAsync(ct);
            if (fresh.Count == 0)
                throw new InvalidOperationException("No visible messages available for extraction.");
            var recent = await history.GetRecentMessagesAsync(c.Id, fresh[0].SequenceNumber - 1, config.RecentMessages, config.RecentTokens, ct);
            var previous = await db.States.Where(x => x.ConversationId == c.Id && x.ProcessedThroughSeq < fresh[0].SequenceNumber).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
            var state = Json.Node(previous?.StateJson ?? "{}")!.AsObject();
            var load = await traces.Start(c, job.TurnId, trace, "load-context", new { fresh = fresh.Select(x => x.Id), recent = recent.Select(x => x.Id) }, config.CapturePayloads, root.Id);
            var scope = new SearchScope(c.UserId, c.Namespace, c.Id, job.ThroughSequence, fresh.Max(x => x.CreatedAt), RetrievalPolicy.IsTaskScoped(config.SearchScope));
            var relevant = await history.MemoryLexicalAsync(scope, string.Join(" ", fresh.Where(x => x.Role == "user").Select(x => x.Content)), config.TopK, ct);
            var memoryIds = relevant.Select(x => x.SourceId).ToArray();
            var existing = await MemoryQueries.ForScope(db, scope).Where(x => memoryIds.Contains(x.Id)).ToListAsync(ct);
            var eligibleMemories = MemoryQueries.ForScope(db, scope);
            var relatedGoals = await db.Knowledge.Where(x => x.UserId == c.UserId && x.Namespace == c.Namespace && x.Kind == "goal" && x.CreatedAt <= scope.AsOf && (!scope.CurrentConversationOnly || x.ConversationId == c.Id && x.MemoryId != null && eligibleMemories.Any(m => m.Id == x.MemoryId))).Take(10).ToListAsync(ct);
            var relatedArtifacts = await db.Artifacts.Where(x => x.UserId == c.UserId && x.Namespace == c.Namespace && x.ConversationId == c.Id && x.CreatedAt <= scope.AsOf).ToListAsync(ct);
            var cachedResolutions = RetrievalPolicy.Latest((await db.Resolutions.Where(x => x.ConversationId == c.Id && fresh.Select(m => m.Id).Contains(x.SourceMessageId)).ToListAsync(ct)).Select(x => Json.Read<ResolvedReference>(x.ResultJson)));
            var resolutions = new List<ResolvedReference>();
            foreach (var user in fresh.Where(x => x.Role == "user"))
                if (cachedResolutions.Any(r => r.SourceMessageId == user.Id))
                    resolutions.AddRange(await resolver.ResolveAsync(c, user, recent, state, config, trace, ct));
            if (config.ExtractionMode == "separated")
                foreach (var user in fresh.Where(x => x.Role == "user"))
                    if (!resolutions.Any(x => x.SourceMessageId == user.Id))
                        resolutions.AddRange(await resolver.ResolveAsync(c, user, recent, state, config, trace, ct));
            await traces.Finish(load, new { newMessages = fresh, recentMessages = recent, conversationState = state, resolvedReferences = resolutions, relevantExistingMemories = existing, relatedGoals, relatedArtifacts }, config.CapturePayloads);
            object Input() => new
            {
                newMessages = fresh,
                recentMessages = recent,
                conversationState = state,
                resolvedReferences = resolutions,
                relevantExistingMemories = existing,
                relatedGoals,
                relatedArtifacts,
                config.ExtractionMode,
                config.AllowSensitiveMemory,
                allowedSearchScope = config.SearchScope
            };
            var execution = await reasoner.Run(c, job.TurnId, trace, "extraction", Input(), config, ct, parent: root.Id);
            var extracted = Json.Read<ExtractionOutput>(execution.Output!);
            if (config.MaxSearchRounds > 0 && extracted.UnresolvedReferences.Count > 0)
            {
                foreach (var message in fresh.Where(x => x.Role == "user"))
                    resolutions.AddRange(await resolver.ResolveRequestsAsync(c, message, recent, state, extracted.UnresolvedReferences.Where(x => x.SourceMessageId == message.Id), config, trace, ct));
                execution = await reasoner.Run(c, job.TurnId, trace, "extraction", Input(), config, ct, parent: root.Id);
                extracted = Json.Read<ExtractionOutput>(execution.Output!);
            }

            if (config.ExtractionMode == "separated")
            {
                var stateRun = await reasoner.Run(c, job.TurnId, trace, "state-update", new { previousState = state, newMessages = fresh, recentMessages = recent, resolvedReferences = resolutions }, config, ct, parent: root.Id);
                extracted = extracted with
                {
                    StateChanges = Json.Read<List<StateChange>>(Json.Node(stateRun.Output!)!["stateChanges"]!.ToJsonString())
                };
            }

            var evidenceIds = resolutions.Where(x => x.Status == "resolved").SelectMany(x => x.Evidence.Select(y => y.MessageId)).ToArray();
            var evidence = await (
                from m in db.Messages
                join conv in db.Conversations on m.ConversationId equals conv.Id
                where evidenceIds.Contains(m.Id) && conv.UserId == c.UserId && conv.Namespace == c.Namespace && (!scope.CurrentConversationOnly || conv.Id == c.Id) && m.SequenceNumber <= conv.VisibleSequence && m.CreatedAt <= scope.AsOf && (m.ConversationId != c.Id || m.SequenceNumber <= job.ThroughSequence)select m).ToListAsync(ct);
            var allowed = fresh.Concat(recent).Concat(evidence).DistinctBy(x => x.Id).ToDictionary(x => x.Id);
            var newIds = fresh.Select(x => x.Id).ToHashSet();
            var validation = await traces.Start(c, job.TurnId, trace, "candidate-validation", extracted.MemoryCandidates, config.CapturePayloads, root.Id);
            var accepted = new List<Candidate>();
            var rejected = new List<object>();
            foreach (var candidate in extracted.MemoryCandidates)
            {
                var errors = EvidenceValidator.ValidateCandidate(candidate, allowed, newIds, config.AllowSensitiveMemory);
                if (errors.Count == 0)
                    accepted.Add(candidate with { Scope = NormalizeScope(candidate.Scope) });
                else
                    rejected.Add(new { candidate, errors });
            }

            await traces.Finish(validation, new { accepted, rejected }, config.CapturePayloads);
            // Retain the rejected delta and permit one targeted repair. Never coerce
            // existing scalar fields into objects or advance past an invalid repair.
            async Task<JsonObject> ApplyState(List<StateChange> delta, bool repair)
            {
                var check = await traces.Start(c, job.TurnId, trace, "state-delta-validation", new { before = state, delta, repair }, config.CapturePayloads, root.Id);
                try
                {
                    var next = StateReducer.Apply(state, delta, allowed.Keys.ToHashSet());
                    await traces.Finish(check, new { valid = true, after = next }, config.CapturePayloads);
                    return next;
                }
                catch (InvalidOperationException ex)
                {
                    await traces.Finish(check, new { valid = false }, config.CapturePayloads, ex.Message);
                    throw;
                }
            }

            JsonObject after;
            try
            {
                after = await ApplyState(extracted.StateChanges, false);
            }
            catch (InvalidOperationException ex)when (config.Operations["state-update"].Enabled)
            {
                var repair = await reasoner.Run(c, job.TurnId, trace, "state-update", new { previousState = state, newMessages = fresh, recentMessages = recent, resolvedReferences = resolutions, rejectedStateChanges = extracted.StateChanges, validationError = ex.Message, allowedSourceMessageIds = allowed.Keys, repairAttempt = 1 }, config, ct, parent: root.Id);
                var delta = Json.Read<List<StateChange>>(Json.Node(repair.Output!)!["stateChanges"]!.ToJsonString());
                after = await ApplyState(delta, true);
                extracted = extracted with
                {
                    StateChanges = delta
                };
            }

            var decisions = new List<(Candidate Candidate, MemoryRecord? Target, string Operation, string Reason)>();
            foreach (var candidate in accepted.DistinctBy(x => CanonicalHash(x)))
            {
                var hash = CanonicalHash(candidate);
                var exact = await db.Memories.SingleOrDefaultAsync(x => x.UserId == c.UserId && x.Namespace == c.Namespace && x.CanonicalHash == hash, ct);
                if (exact != null)
                {
                    decisions.Add((candidate, exact, "ADD_EVIDENCE", "Exact canonical match"));
                    continue;
                }

                var searchSpan = await traces.Start(c, job.TurnId, trace, "reconciliation-candidate-search", candidate, config.CapturePayloads, root.Id);
                var lexical = await history.MemoryLexicalAsync(scope, candidate.Text, 12, ct);
                IReadOnlyList<SearchCandidate> semantic = [];
                string? searchError = null;
                try
                {
                    semantic = (await (config.MemoryProvider == "mem0" ? (IMemoryStore)mem0 : postgres).SearchAsync(new(scope, candidate.Text, 12), ct)).Items;
                }
                catch (Exception ex)
                {
                    searchError = ex.Message;
                }

                var relatedIds = lexical.Concat(semantic).Select(x => x.SourceId).ToArray();
                var matches = await MemoryQueries.ForScope(db, scope).Where(x => x.Subject == candidate.Subject && (x.Predicate == candidate.Predicate || relatedIds.Contains(x.Id)) && (x.Scope == candidate.Scope || candidate.Scope == "global" && (x.Scope == "general" || x.Scope == "user" || x.Scope == "user_profile")) && x.ValidFrom <= scope.AsOf && x.Status != "superseded").OrderByDescending(x => x.ValidFrom).Take(8).ToListAsync(ct);
                await traces.Finish(searchSpan, new { lexical, semantic, matches, semanticSearchError = searchError }, config.CapturePayloads);
                if (matches.Count == 0 || !config.Operations["reconciliation"].Enabled)
                {
                    decisions.Add((candidate, null, "CREATE", "No exact subject/predicate/scope match"));
                    continue;
                }

                var reconciliation = await reasoner.Run(c, job.TurnId, trace, "reconciliation", new { candidate, existingMemories = matches }, config, ct, parent: root.Id);
                var d = Json.Node(reconciliation.Output!)!;
                var operation = d["operation"]!.ToString();
                var targetId = Guid.TryParse(d["targetMemoryId"]?.ToString(), out var parsed) ? parsed : Guid.Empty;
                var target = matches.FirstOrDefault(x => x.Id == targetId);
                if (operation is "SUPERSEDE" or "ADD_EVIDENCE" or "MARK_CONTRADICTION" && target == null)
                    throw new InvalidOperationException("Reconciliation selected an unauthorized memory target");
                if (operation == "SUPERSEDE" && (candidate.Status != "confirmed" || NormalizeScope(target!.Scope) != candidate.Scope))
                    throw new InvalidOperationException("Only confirmed same-scope updates may supersede memory");
                decisions.Add((candidate, target, operation, d["reason"]!.ToString()));
            }

            var persist = await traces.Start(c, job.TurnId, trace, "reconciliation-and-persistence", decisions.Select(x => new { x.Candidate, targetId = x.Target?.Id, x.Operation, x.Reason }), config.CapturePayloads, root.Id);
            var changes = new List<object>();
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                foreach (var d in decisions)
                {
                    if (d.Operation == "REJECT")
                    {
                        changes.Add(new { d.Operation, d.Reason, candidate = d.Candidate });
                        continue;
                    }

                    var introducedAt = d.Candidate.SourceEvidence.Where(e => newIds.Contains(e.MessageId) && allowed[e.MessageId].Role is "user" or "tool").Select(e => allowed[e.MessageId].CreatedAt).DefaultIfEmpty(scope.AsOf).Max();
                    var memory = d.Target;
                    if (d.Operation is "SUPERSEDE" or "MARK_CONTRADICTION")
                    {
                        var before = Json.Write(memory);
                        memory!.Status = d.Operation == "SUPERSEDE" ? "superseded" : "contradicted";
                        memory.ValidTo = d.Operation == "SUPERSEDE" ? introducedAt : null;
                        memory.Version++;
                        db.MemoryRevisions.Add(new MemoryRevision { MemoryId = memory.Id, Version = memory.Version, Operation = d.Operation, SnapshotJson = Json.Write(memory), Reason = d.Reason, RunId = trace });
                        changes.Add(new { d.Operation, before = Json.Node(before), after = memory });
                        memory = null;
                    }

                    if (memory == null)
                    {
                        memory = new MemoryRecord
                        {
                            UserId = c.UserId,
                            Namespace = c.Namespace,
                            Type = d.Candidate.Type,
                            Subject = d.Candidate.Subject,
                            Predicate = d.Candidate.Predicate,
                            ValueJson = Json.Write(d.Candidate.Value),
                            CanonicalText = d.Candidate.Text,
                            CanonicalHash = CanonicalHash(d.Candidate),
                            Status = d.Candidate.Status,
                            Scope = d.Candidate.Scope,
                            ValidFrom = introducedAt
                        };
                        db.Memories.Add(memory);
                        db.MemoryRevisions.Add(new MemoryRevision { MemoryId = memory.Id, Version = 1, Operation = "CREATE", SnapshotJson = Json.Write(memory), Reason = d.Reason, RunId = trace });
                        if (memory.Type is "goal" or "plan" or "entity" or "topic" or "relationship")
                        {
                            var kind = memory.Type == "plan" ? "goal" : memory.Type;
                            db.Knowledge.Add(new KnowledgeRecord { UserId = c.UserId, Namespace = c.Namespace, Kind = kind, Name = memory.CanonicalText, Status = memory.Status == "proposed" ? "proposed" : "active", ConversationId = c.Id, MemoryId = memory.Id, DataJson = memory.ValueJson, CreatedAt = scope.AsOf });
                        }
                    }
                    else
                    {
                        memory.Version++;
                        db.MemoryRevisions.Add(new MemoryRevision { MemoryId = memory.Id, Version = memory.Version, Operation = "ADD_EVIDENCE", SnapshotJson = Json.Write(memory), Reason = d.Reason, RunId = trace });
                    }

                    foreach (var source in d.Candidate.SourceEvidence)
                        if (!await db.MemorySources.AnyAsync(x => x.MemoryId == memory.Id && x.SourceId == source.MessageId && x.SourceRole == source.Role, ct) && !db.MemorySources.Local.Any(x => x.MemoryId == memory.Id && x.SourceId == source.MessageId && x.SourceRole == source.Role))
                            db.MemorySources.Add(new MemorySource { MemoryId = memory.Id, SourceId = source.MessageId, SourceRole = source.Role, EvidenceQuote = source.Quote, EvidenceStart = allowed[source.MessageId].Content.IndexOf(source.Quote, StringComparison.Ordinal), ExtractionRunId = trace });
                    db.Jobs.Add(new Job { ConversationId = c.Id, TurnId = job.TurnId, ThroughSequence = job.ThroughSequence, Kind = "sync", Key = $"sync:{memory.Id}:{memory.Version}", PayloadJson = Json.Write(new { memoryId = memory.Id, provider = config.MemoryProvider }) });
                    changes.Add(new { operation = d.Operation, memory, d.Reason });
                }

                var snapshot = new StateSnapshot
                {
                    ConversationId = c.Id,
                    TurnId = job.TurnId,
                    Version = (previous?.Version ?? 0) + 1,
                    ProcessedThroughSeq = job.ThroughSequence,
                    StateJson = after.ToJsonString(),
                    BeforeJson = state.ToJsonString(),
                    DeltaJson = Json.Write(extracted.StateChanges)
                };
                db.States.Add(snapshot);
                c.LastProcessedSequence = job.ThroughSequence;
                if (config.ConsolidateEveryTurns > 0 && snapshot.Version % config.ConsolidateEveryTurns == 0)
                    db.Jobs.Add(new Job { ConversationId = c.Id, TurnId = job.TurnId, ThroughSequence = job.ThroughSequence, Kind = "consolidate", Key = $"consolidate:{c.Id}:{job.ThroughSequence}" });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            await traces.Finish(persist, new { changes, stateBefore = state, stateAfter = after, delta = extracted.StateChanges, checkpoint = c.LastProcessedSequence }, config.CapturePayloads);
            await traces.Finish(root, new { accepted = accepted.Count, rejected = rejected.Count, checkpoint = c.LastProcessedSequence }, config.CapturePayloads);
            await bus.Send(c.Id, "memory.updated", new { job.TurnId, changes }, ct);
        }
        catch (Exception ex)
        {
            await traces.Finish(root, null, config.CapturePayloads, ex.Message);
            throw;
        }
    }

    public static string NormalizeScope(string value) => value.Trim().ToLowerInvariant()is "general" or "global" or "user" or "user_profile" ? "global" : value.Trim();
    public static string CanonicalHash(Candidate c) => TraceRecorder.Hash(string.Join('|', c.Subject, c.Predicate, NormalizeScope(c.Scope), c.Text.Trim().ToLowerInvariant()));
    public async Task Consolidate(Job job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var c = await db.Conversations.SingleAsync(x => x.Id == job.ConversationId, ct);
        var config = await settings.Get(c, ct);
        var prior = await db.Knowledge.Where(x => x.ConversationId == c.Id && x.Kind == "episode").OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var after = prior == null ? 0 : Json.Node(prior.DataJson)?["endSequence"]?.GetValue<long>() ?? 0;
        var messages = await db.Messages.Where(x => x.ConversationId == c.Id && x.SequenceNumber > after && x.SequenceNumber <= job.ThroughSequence && x.SequenceNumber <= c.VisibleSequence).OrderBy(x => x.SequenceNumber).ToListAsync(ct);
        if (messages.Count == 0)
            return;
        var result = await reasoner.Run(c, job.TurnId, Guid.NewGuid(), "summarization", new { messages }, config, ct);
        var output = Json.Node(result.Output!)!;
        var ids = output["sourceMessageIds"]!.AsArray().Select(x => Guid.Parse(x!.ToString())).ToArray();
        if (ids.Any(x => !messages.Any(m => m.Id == x)))
            throw new InvalidOperationException("Episode cites unknown evidence");
        var record = new KnowledgeRecord
        {
            UserId = c.UserId,
            Namespace = c.Namespace,
            Kind = "episode",
            Name = output["summary"]!.ToString(),
            ConversationId = c.Id,
            CreatedAt = messages.Max(x => x.CreatedAt),
            DataJson = Json.Write(new { startSequence = messages[0].SequenceNumber, endSequence = job.ThroughSequence, summary = output })
        };
        db.Knowledge.Add(record);
        foreach (var topic in output["topics"]!.AsArray())
            db.Knowledge.Add(new KnowledgeRecord { UserId = c.UserId, Namespace = c.Namespace, Kind = "topic", Name = topic!.ToString(), ConversationId = c.Id, CreatedAt = record.CreatedAt, DataJson = Json.Write(new { episodeId = record.Id, sourceMessageIds = ids }) });
        await db.SaveChangesAsync(ct);
    }
}
