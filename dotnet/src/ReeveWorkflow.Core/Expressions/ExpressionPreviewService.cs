using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Expressions;

/// <summary>Result envelope for the preview endpoint and any other ad-hoc callers. <see cref="Type"/> is the closed term the typer
/// inferred for the expression and <see cref="Value"/> the value the typed evaluator answered, in the CLR type of that term.</summary>
public sealed record ExpressionPreviewResult(
    bool Ok,
    object? Value,
    string? Type,
    ExpressionPreviewError? Error,
    long ElapsedMicroseconds);

/// <summary>Why a preview has no value. <see cref="RuleId"/> is the compiler rule the expression breaks, or <c>expression-runtime-error</c>
/// when it is well typed and its evaluation failed; null for a failure with neither.</summary>
public sealed record ExpressionPreviewError(
    string Message,
    int Position,
    string? Suggestion,
    string? RuleId = null);

/// <summary>Caches pre-built contexts by scope so the preview endpoint need not rebuild a device-state
/// snapshot on every keystroke.</summary>
public sealed class ExpressionPreviewService
{
    /// <summary>The service key the library registers its own instance under, so that the unkeyed service
    /// type stays the host's to claim.</summary>
    /// <remarks>A library default registered unkeyed would be added where <c>AddWorkflows</c> is called and
    /// would beat a host's own <c>TryAdd</c> written after it — and a host that mounted the preview endpoint earlier registers this type itself, so that is a shape adopters are in. Keyed, the
    /// library's instance answers only where the endpoint asks for it by name, and it asks for it second.</remarks>
    public const string LibraryServiceKey = "ReeveWorkflow.ExpressionPreviewService.Library";

    private readonly IHelperResolver _helperResolver;
    private readonly IReadOnlyList<WorkflowExpressionNamespace> _namespaceRegistrations;
    private readonly bool _readsHostZone;
    private readonly TimeZoneInfo? _timeZone;
    private readonly Func<IServiceProvider, CancellationToken, Task<TimeZoneInfo>>? _resolveTimeZone;
    private readonly ConcurrentDictionary<string, CachedContext> _cache = new(StringComparer.Ordinal);

    public ExpressionPreviewService(IHelperResolver? helperResolver = null)
        : this(helperResolver, [])
    {
    }

    /// <summary>Creates the preview service with host namespaces for the library's preview endpoint.</summary>
    public ExpressionPreviewService(
        IHelperResolver? helperResolver,
        IReadOnlyList<WorkflowExpressionNamespace> namespaceRegistrations)
    {
        _helperResolver = helperResolver ?? NullHelperResolver.Instance;
        _namespaceRegistrations = namespaceRegistrations ?? throw new ArgumentNullException(nameof(namespaceRegistrations));
    }

    /// <summary>Creates the preview service with host namespaces and the zone its local-time fields
    /// (<c>now.localHour</c> etc.) are read in, resolved the way a workflow run resolves it: through
    /// <paramref name="resolveTimeZone"/>, from the service scope passed to <see cref="PreviewAsync"/>, when it is set,
    /// and as <paramref name="timeZone"/> otherwise. Pass the zone and the lookup the host gives its runs, so a preview
    /// reads the wall clock a run would.</summary>
    /// <remarks>A context pinned with <see cref="SetContext"/> that sets its own <see cref="ExpressionContext.LocalNow"/>
    /// keeps it, and the zone is not resolved for it. A null zone, given here or answered by the lookup, fails a preview
    /// that reads local time with a preview error, as it fails a run. The other constructors read the local-time fields
    /// from the scope's <see cref="ExpressionContext.LocalNow"/>, which is UTC unless a pinned context sets it.</remarks>
    public ExpressionPreviewService(
        IHelperResolver? helperResolver,
        IReadOnlyList<WorkflowExpressionNamespace> namespaceRegistrations,
        TimeZoneInfo timeZone,
        Func<IServiceProvider, CancellationToken, Task<TimeZoneInfo>>? resolveTimeZone = null)
        : this(helperResolver, namespaceRegistrations)
    {
        _readsHostZone = true;
        _timeZone = timeZone;
        _resolveTimeZone = resolveTimeZone;
    }

    private sealed record CachedContext(ExpressionContext Context, DateTimeOffset CachedAt);

    private ExpressionPreviewResult Evaluate(string expression, ExpressionContext ctx, DateTimeOffset localNow,
        IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer> checks, IReadOnlyDictionary<HelperPath, object?> helpers)
    {
        var sw = Stopwatch.StartNew();
        var body = expression ?? string.Empty;
        if (ExpressionSource.IsExpression(body))
            body = ExpressionSource.Strip(body);

        ExpressionPreviewError? error = null;
        object? value = null;
        string? type = null;
        try
        {
            var typed = ExpressionTyper.Type(ExpressionSyntaxCache.ParseCached(body), TypingScope(ctx, checks));
            if (typed.Tree is { } tree)
            {
                value = TypedExpressionEvaluator.Evaluate(tree, EvaluationScope(ctx, localNow, helpers));
                type = tree.Term.ToString();
            }
            else
            {
                var refusal = typed.Refusal;
                error = refusal is null
                    ? new ExpressionPreviewError("a value the expression reads has no term in this preview", 0, null)
                    : new ExpressionPreviewError(refusal.Message, refusal.Position, null, refusal.RuleId);
            }
        }
        catch (ExpressionSyntaxException ex)
        {
            error = new ExpressionPreviewError(ex.Message, ex.Position, ex.Suggestion, CompilationRules.ExpressionSyntax);
        }
        catch (ExpressionEvaluationException ex)
        {
            error = new ExpressionPreviewError(ex.Message, ex.Position, ex.Suggestion, ex.Code);
        }

        sw.Stop();
        return new ExpressionPreviewResult(
            Ok: error is null, Value: error is null ? value : null, Type: type, Error: error,
            ElapsedMicroseconds: sw.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000));
    }

    private static ExpressionTypingScope TypingScope(ExpressionContext ctx, IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer> checks)
    {
        var builder = new ExpressionTypingScope.Builder().Vocabulary(ctx.TypeVocabulary).Helper(checks);
        foreach (var binding in ctx.Hosts)
            builder.Namespace(binding);
        return builder.Build();
    }

    private static ExpressionEvaluationScope EvaluationScope(
        ExpressionContext ctx, DateTimeOffset localNow, IReadOnlyDictionary<HelperPath, object?> helpers)
    {
        var builder = new ExpressionEvaluationScope.Builder()
            .Vocabulary(ctx.TypeVocabulary)
            .Now(ctx.Now, localNow)
            .Helper(helpers);
        foreach (var binding in ctx.Hosts)
            builder.Namespace(binding);
        return builder.Build();
    }

    /// <summary>Evaluates a preview with the registered host namespaces and the caller's service scope.</summary>
    public async Task<ExpressionPreviewResult> PreviewAsync(
        string expression, string contextScope, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var typeVocabulary = ExpressionNamespaceValues.VocabularyOf(services);
        var baseContext = ResolveContext(contextScope);
        var referencedNamespaces = ReferencedNamespaces(expression);
        var relevant = _namespaceRegistrations.Where(registration => referencedNamespaces.Contains(registration.Namespace)).ToArray();
        IReadOnlyCollection<ExpressionNamespaceBinding> resolvers;
        try
        {
            resolvers = await WorkflowExpressionNamespaces.MergeResolversAsync(
                relevant, new ExpressionNamespaceScope(services, null, null, null), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExpressionPreviewResult(false, null, null,
                new ExpressionPreviewError($"Could not resolve host expression namespaces: {ex.Message}", 0, null), 0);
        }
        var shared = resolvers.Select(r => r.Resolver.Namespace).FirstOrDefault(name => baseContext.Namespaces.Contains(name));
        if (shared is not null)
            return new ExpressionPreviewResult(false, null, null,
                new ExpressionPreviewError(
                    $"Namespace '{shared}' is both set on this scope's preview context and registered by the host; keep one.",
                    0, null), 0);
        var context = baseContext.WithNamespaces(resolvers, typeVocabulary);
        var body = expression ?? string.Empty;
        if (ExpressionSource.IsExpression(body)) body = ExpressionSource.Strip(body);
        try { _ = ExpressionSyntaxCache.ParseCached(body); }
        catch (ExpressionSyntaxException ex)
        {
            return new ExpressionPreviewResult(false, null, null,
                new ExpressionPreviewError(ex.Message, ex.Position, ex.Suggestion, CompilationRules.ExpressionSyntax), 0);
        }
        var localNow = context.LocalNow;
        if (_readsHostZone && !context.LocalNowPinned && referencedNamespaces.Contains("now"))
        {
            TimeZoneInfo? zone;
            try
            {
                zone = await WorkflowHomeTimeZone.ResolveAsync(_timeZone!, _resolveTimeZone, services, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ExpressionPreviewResult(false, null, null,
                    new ExpressionPreviewError($"Could not resolve the host's time zone: {ex.Message}", 0, null), 0);
            }
            if (zone is null)
                return new ExpressionPreviewResult(false, null, null,
                    new ExpressionPreviewError("The host has no time zone to read local time in.", 0, null), 0);
            localNow = TimeZoneInfo.ConvertTime(context.Now, zone);
        }
        var paths = new HashSet<HelperPath>();
        try
        {
            foreach (var token in new ExpressionLexer(body).Tokenize().Where(t => t.Kind == TokenKind.Variable))
                if (ExpressionPath.TrySplit(token.Text, ExpressionKeyShapes.For(_namespaceRegistrations), out var parts, out _) && parts.Namespace == "helper")
                    paths.Add(new HelperPath(parts.Key));
        }
        catch (ExpressionSyntaxException) { }
        try
        {
            var checks = paths.Count == 0 ? new Dictionary<HelperPath, NamespacePathCheckAnswer>()
                : await context.Helper.CheckAsync(paths, cancellationToken).ConfigureAwait(false);
            var typed = ExpressionTyper.Type(ExpressionSyntaxCache.ParseCached(body), TypingScope(context, checks));
            IReadOnlyDictionary<HelperPath, object?> helpers;
            if (typed.Tree is { } tree && tree.HelperPaths.Count > 0)
            {
                try { helpers = await context.Helper.ReadAsync(tree.HelperPaths, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new ExpressionPreviewResult(false, null, null,
                        new ExpressionPreviewError($"Reading helper values failed: {ex.Message}", 0, null, WorkflowErrorCodes.ExpressionRuntimeError), 0);
                }
            }
            else helpers = new Dictionary<HelperPath, object?>();
            return Evaluate(expression, context, localNow, checks, helpers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExpressionPreviewResult(false, null, null,
                new ExpressionPreviewError($"Could not resolve helper values: {ex.Message}", 0, null), 0);
        }
    }

    private static HashSet<string> ReferencedNamespaces(string? expression)
    {
        var body = expression ?? string.Empty;
        if (ExpressionSource.IsExpression(body))
            body = ExpressionSource.Strip(body);
        try
        {
            return new ExpressionLexer(body).Tokenize()
                .Where(token => token.Kind == TokenKind.Variable)
                .Select(token => ExpressionPath.NamespaceOf(token.Text))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (ExpressionSyntaxException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Pin a pre-built context for a scope, for five minutes. Purely a latency optimisation.</summary>
    public void SetContext(string scope, ExpressionContext context)
    {
        _cache[scope] = new CachedContext(context, DateTimeOffset.UtcNow);
    }

    private ExpressionContext ResolveContext(string? scope)
    {
        if (string.IsNullOrEmpty(scope) || scope == "global")
            return new ExpressionContext(helperResolver: _helperResolver);

        if (_cache.TryGetValue(scope, out var cached))
        {
            if (DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(5))
                return cached.Context;
            _cache.TryRemove(scope, out _);
        }
        return new ExpressionContext(helperResolver: _helperResolver);
    }
}
