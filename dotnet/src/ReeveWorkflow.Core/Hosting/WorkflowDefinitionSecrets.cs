using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>The library's <see cref="IWorkflowDefinitionSecrets"/> over the blocks a host registered and its codec, if any. Registered
/// scoped beside the blocks, because the codec may be.</summary>
/// <remarks>A save with origin <see cref="WorkflowDefinitionOrigin.Edited"/> is refused where an entry cannot be kept; with origin
/// <see cref="WorkflowDefinitionOrigin.Imported"/> that entry is left out and reported. A key that is no field name is refused either way.</remarks>
public sealed class WorkflowDefinitionSecrets(IWorkflowBlocks blocks, IWorkflowSecretCodec? codec) : IWorkflowDefinitionSecrets
{
    private const string ProtectPrefix = "protect:";
    private const string Masked = WorkflowSecretEntries.Masked;

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = StoredDocumentLexer.MaxNesting };

    private readonly IWorkflowBlocks blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));

    /// <inheritdoc />
    public string Mask(string definitionJson)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        if (StoredDocumentLexer.IsBlank(definitionJson) || StoredDocumentLexer.Lex(definitionJson, out _) is not { } bytes)
            return definitionJson;

        using var document = JsonDocument.Parse(bytes, ParseOptions);
        var edits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in WorkflowSecretEntries.Walk(document.RootElement, blocks))
        {
            if (entry.Value != Masked && WorkflowSecretEntries.IsMasked(codec, entry.Key, entry.Value))
                edits[entry.Pointer] = Masked;
        }

        return edits.Count == 0 ? definitionJson : JsonTextSplice.Apply(definitionJson, new Dictionary<string, string>(), edits);
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowSecretAdmission> AdmitAsync(
        WorkflowSecretSave save, string? replacedDefinition, string definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(definition);
        if (!Enum.IsDefined(save.Origin))
            throw new ArgumentOutOfRangeException(nameof(save), save.Origin, "The save's origin is neither Edited nor Imported.");

        var stored = StoredEntries(save, replacedDefinition);
        var final = new List<Decided>();
        var edits = new Dictionary<string, string>(StringComparer.Ordinal);
        var removals = new List<string>();
        var dropped = new List<WorkflowDroppedSecret>();
        var entries = Read(definition, includeNonText: true);
        foreach (var entry in entries)
        {
            if (FieldNames.FirstNonToken(entry.Key) >= 0)
            {
                throw Refusal(WorkflowSaveRefusalCodes.SecretKeyInvalid, PlaceOf(save, entry), entry.Row,
                    "is not a field name: a name is a token of letters, digits and the marks !#$%&'*+-.^_`|~, so a header written as a line goes in as a key and a value");
            }
        }

        foreach (var entry in entries.Where(e => e.IsText))
        {
            var place = PlaceOf(save, entry);
            stored.TryGetValue(place, out var kept);
            var decision = await DecideAsync(save, place, entry.Value, kept, entry.Row, cancellationToken).ConfigureAwait(false);
            if (decision.DropReason is { } reason)
            {
                removals.Add(entry.Pointer);
                dropped.Add(Dropped(place, reason));
                continue;
            }

            final.Add(new(place, decision.Value!, entry.Pointer));
            if (!string.Equals(decision.Value, entry.Value, StringComparison.Ordinal))
                edits[entry.Pointer] = decision.Value!;
        }

        var (bound, unbound) = ReferenceDelta(stored, final);
        if (await AskAsync(save, bound, unbound, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            if (save.Origin != WorkflowDefinitionOrigin.Imported || bound.Count == 0)
                throw ReferencesRefused(refused);

            foreach (var binding in final.Where(f => Binds(stored, f)).ToList())
            {
                final.Remove(binding);
                edits.Remove(binding.Pointer);
                removals.Add(binding.Pointer);
                dropped.Add(Dropped(binding.Place, $"{WorkflowDroppedSecretReasons.ReferenceRefused}: {refused}"));
            }

            (bound, unbound) = ReferenceDelta(stored, final);
            if (await AskAsync(save, bound, unbound, cancellationToken).ConfigureAwait(false) is { } refusedAgain)
                throw ReferencesRefused(refusedAgain);
        }

        var answered = edits.Count == 0 ? definition : JsonTextSplice.Apply(definition, new Dictionary<string, string>(), edits);
        if (removals.Count > 0)
            answered = JsonTextSplice.Edit(answered, removals, []);
        return new WorkflowSecretAdmission(answered, bound, unbound) { DroppedSecrets = dropped };
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowSecretAdmission> AdmitRestoredAsync(
        WorkflowSecretSave save, string? replacedDefinition, string definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (save.Origin != WorkflowDefinitionOrigin.Edited)
            throw new NotSupportedException("A restored version is a copy of the workflow's own, so only a save with origin Edited restores one.");
        ArgumentNullException.ThrowIfNull(definition);
        var stored = StoredEntries(save, replacedDefinition);
        var final = Read(definition).Select(entry => new Decided(PlaceOf(save, entry), entry.Value, entry.Pointer)).ToList();
        var (bound, dropped) = ReferenceDelta(stored, final);
        if (await AskAsync(save, bound, dropped, cancellationToken).ConfigureAwait(false) is { } refused)
            throw ReferencesRefused(refused);
        return new WorkflowSecretAdmission(definition, bound, dropped);
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkflowDroppedSecret> PredictImportDrops(string definitionJson)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        if (StoredDocumentLexer.IsBlank(definitionJson) || StoredDocumentLexer.Lex(definitionJson, out _) is not { } bytes)
            return [];

        using var document = JsonDocument.Parse(bytes, ParseOptions);
        var predicted = new List<WorkflowDroppedSecret>();
        foreach (var entry in WorkflowSecretEntries.Walk(document.RootElement, blocks))
        {
            var reason = entry.Value == Masked ? WorkflowDroppedSecretReasons.Masked
                : !entry.Value.StartsWith(ProtectPrefix, StringComparison.Ordinal) && codec?.Classify(entry.Value) == WorkflowSecretForm.Protected
                    ? WorkflowDroppedSecretReasons.ForeignProtected
                    : null;
            if (reason is not null)
                predicted.Add(Dropped(PlaceOf(Guid.Empty, entry), reason));
        }

        return predicted;
    }

    private async ValueTask<Decided> DecideAsync(
        WorkflowSecretSave save, Place place, string value, string? kept, int row, CancellationToken cancellationToken)
    {
        if (string.Equals(value, Masked, StringComparison.Ordinal))
        {
            if (kept is null)
            {
                return Refuse(save, WorkflowSaveRefusalCodes.SecretValueUnmatched, WorkflowDroppedSecretReasons.Masked, place, row,
                    "is '***', and the version this save replaces holds no entry at that place to keep. Send the value again as 'protect:<value>', or remove the entry");
            }

            return codec is not null && codec.Classify(kept) == WorkflowSecretForm.Plain && WorkflowSensitiveNames.Matches(place.Site.Key)
                ? await ProtectAsync(save, place, kept, row, cancellationToken).ConfigureAwait(false)
                : new(place, kept);
        }

        if (value.StartsWith(ProtectPrefix, StringComparison.Ordinal))
        {
            if (codec is null)
            {
                return Refuse(save, WorkflowSaveRefusalCodes.SecretCodecMissing, WorkflowDroppedSecretReasons.CodecMissing, place, row,
                    "is a 'protect:' value, and this host registered no secret codec to protect it");
            }

            return await ProtectAsync(save, place, value[ProtectPrefix.Length..], row, cancellationToken).ConfigureAwait(false);
        }

        if (codec is null)
            return new(place, value);

        switch (codec.Classify(value))
        {
            case WorkflowSecretForm.Reference:
                return new(place, value);
            case WorkflowSecretForm.Protected:
                return string.Equals(value, kept, StringComparison.Ordinal)
                    ? new(place, value)
                    : Refuse(save, WorkflowSaveRefusalCodes.SecretValueUnmatched, WorkflowDroppedSecretReasons.ForeignProtected, place, row,
                        "is a protected value that is not the one this workflow stored at that place. Send the value again as 'protect:<value>'");
            default:
                return WorkflowSensitiveNames.Matches(place.Site.Key)
                    ? await ProtectAsync(save, place, value, row, cancellationToken).ConfigureAwait(false)
                    : new(place, value);
        }
    }

    private static Decided Refuse(WorkflowSecretSave save, string code, string reason, Place place, int row, string what) =>
        save.Origin == WorkflowDefinitionOrigin.Imported
            ? new(place, null, DropReason: reason)
            : throw Refusal(code, place, row, what);

    private async ValueTask<Decided> ProtectAsync(
        WorkflowSecretSave save, Place place, string plaintext, int row, CancellationToken cancellationToken)
    {
        try
        {
            return new(place, await codec!.ProtectAsync(place.Site, plaintext, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The secret codec protected a value as nothing."));
        }
        catch (WorkflowSecretRefusedException refused)
        {
            return Refuse(save, WorkflowSaveRefusalCodes.SecretRefused, $"{WorkflowDroppedSecretReasons.ProtectRefused}: {refused.Message}", place, row,
                $"was not protected: the host refused it ({refused.Message})");
        }
    }

    private async ValueTask<string?> AskAsync(
        WorkflowSecretSave save, IReadOnlyList<string> bound, IReadOnlyList<string> unbound, CancellationToken cancellationToken)
    {
        if (codec is null || bound.Count + unbound.Count == 0)
            return null;

        try
        {
            await codec.AdmitReferencesAsync(save, bound, unbound, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (WorkflowSecretRefusedException refused)
        {
            return refused.Message;
        }
    }

    private static WorkflowSecretSaveRefusedException ReferencesRefused(string reason) =>
        new(WorkflowSaveRefusalCodes.SecretRefused, $"The host refused the secret references this save binds or drops ({reason}).");

    private bool Binds(Dictionary<Place, string> stored, Decided entry) =>
        codec?.Classify(entry.Value!) == WorkflowSecretForm.Reference
        && !(stored.TryGetValue(entry.Place, out var before) && string.Equals(before, entry.Value, StringComparison.Ordinal));

    private (IReadOnlyList<string> Bound, IReadOnlyList<string> Dropped) ReferenceDelta(
        Dictionary<Place, string> stored, List<Decided> final)
    {
        if (codec is null)
            return ([], []);

        var bound = new List<string>();
        var dropped = new List<string>();
        var now = new Dictionary<Place, string>();
        foreach (var (place, value) in final.Select(f => (f.Place, f.Value!)))
        {
            now[place] = value;
            if (codec.Classify(value) == WorkflowSecretForm.Reference
                && !(stored.TryGetValue(place, out var before) && string.Equals(before, value, StringComparison.Ordinal))
                && !bound.Contains(value))
            {
                bound.Add(value);
            }
        }

        foreach (var (place, before) in stored)
        {
            if (codec.Classify(before) == WorkflowSecretForm.Reference
                && !(now.TryGetValue(place, out var after) && string.Equals(after, before, StringComparison.Ordinal))
                && !dropped.Contains(before))
            {
                dropped.Add(before);
            }
        }

        return (bound, dropped);
    }

    private Dictionary<Place, string> StoredEntries(WorkflowSecretSave save, string? replacedDefinition)
    {
        var stored = new Dictionary<Place, string>();
        if (StoredDocumentLexer.IsBlank(replacedDefinition) || StoredDocumentLexer.Lex(replacedDefinition, out _) is not { } bytes)
            return stored;

        using var document = JsonDocument.Parse(bytes, ParseOptions);
        foreach (var entry in WorkflowSecretEntries.Walk(document.RootElement, blocks))
            stored[PlaceOf(save, entry)] = entry.Value;
        return stored;
    }

    private List<WorkflowSecretEntry> Read(string definition, bool includeNonText = false)
    {
        if (StoredDocumentLexer.IsBlank(definition) || StoredDocumentLexer.Lex(definition, out _) is not { } bytes)
            throw new InvalidWorkflowDefinitionException("it is not valid JSON.");

        using var document = JsonDocument.Parse(bytes, ParseOptions);
        if (document.RootElement.ValueKind == JsonValueKind.Null)
            throw new InvalidWorkflowDefinitionException("it is the JSON null.");
        return WorkflowSecretEntries.Walk(document.RootElement, blocks, includeNonText);
    }

    private static Place PlaceOf(WorkflowSecretSave save, WorkflowSecretEntry entry) => PlaceOf(save.WorkflowId, entry);

    private static Place PlaceOf(Guid workflowId, WorkflowSecretEntry entry) =>
        new(new(workflowId, WorkflowSecretSite.CanvasPathText(entry.Canvas), entry.NodeId, entry.Input, entry.Key), entry.EdgeId);

    private static WorkflowSecretSaveRefusedException Refusal(string code, Place place, int row, string what)
    {
        var site = place.Site;
        var canvas = site.CanvasPath.Length == 0 ? "" : $" in the sub-canvas '{site.CanvasPath}'";
        var edge = place.EdgeId is null ? "" : $" on the literal mapping of edge '{place.EdgeId}'";
        return new WorkflowSecretSaveRefusedException(
            code, $"Input '{site.Input}' {FieldNames.Naming(site.Key, row)} of node '{site.NodeId}'{canvas}{edge} {what}.");
    }

    private readonly record struct Decided(Place Place, string? Value, string Pointer = "", string? DropReason = null);

    private static WorkflowDroppedSecret Dropped(Place place, string reason) =>
        new(place.Site.CanvasPath, place.Site.NodeId, place.Site.Input, place.Site.Key, place.EdgeId, reason);

    private readonly record struct Place(WorkflowSecretSite Site, string? EdgeId);
}
