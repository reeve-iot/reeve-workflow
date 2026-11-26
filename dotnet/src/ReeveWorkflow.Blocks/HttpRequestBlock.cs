using System.Net.Http.Headers;
using System.Text;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Blocks;

/// <summary>Sends one HTTP request and reports its status, body and success. A host that guards outbound
/// traffic or pools its clients derives from this and fills the three hooks below rather than rewriting it.</summary>
public class HttpRequestBlock : IWorkflowBlockPlugin
{
    private const long MaxTimeoutSeconds = (uint.MaxValue - 1) / 1000;
    private const int MaxQuotedBodyCharacters = 1024;

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>An <see cref="HttpClient"/> to send with, and whether the block must dispose it afterwards.</summary>
    /// <param name="Client">The client the request is sent on.</param>
    /// <param name="CallerOwnsClient">True when the block created this client for one call; false for a pooled
    /// or shared one, whose lifetime belongs to whoever handed it over.</param>
    protected readonly record struct HttpClientLease(HttpClient Client, bool CallerOwnsClient);

    /// <summary>Answers which client this request goes out on. Override to take one from an
    /// <c>IHttpClientFactory</c> pool or to wrap a handler around it; say in the lease who disposes it.
    /// The client must keep no cookies (<c>UseCookies = false</c>): a jar would carry one run's session into
    /// another and merge it into an author's <c>Cookie</c> entry.</summary>
    protected virtual HttpClientLease ResolveHttpClient(WorkflowBlockContext context) =>
        new(SharedClient, CallerOwnsClient: false);

    /// <summary>Called with the URL before anything is sent. Throw <see cref="WorkflowEgressBlockedException"/>
    /// to refuse it; the default permits every URL, because only the host knows where it may talk to.</summary>
    protected virtual void ValidateUrl(string url, WorkflowBlockContext context)
    {
    }

    /// <summary>Whether this exception means the request was refused rather than failed. Override when a
    /// host's own guard throws its own type — one blocked at connect time never passes through ValidateUrl.</summary>
    protected virtual bool IsEgressBlocked(Exception exception) => exception is WorkflowEgressBlockedException;

    /// <summary>The most bytes of response body the block reads, 4 MiB unless overridden. A longer body fails the
    /// request as a network failure does: <c>Success</c> false with the reason in <c>Body</c>, or a throw under
    /// <c>ThrowOnFailure</c>. Override to give a host's own ceiling.</summary>
    protected virtual long MaxResponseBodyBytes => 4 * 1024 * 1024;

    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var url = BlockInputCoercion.String(inputs, "Url")
            ?? throw new ArgumentException("Input 'Url' is required.");

        var method = BlockInputCoercion.String(inputs, "Method") ?? "GET";
        var body = BlockInputCoercion.String(inputs, "Body");
        var timeoutSeconds = BlockInputCoercion.Within(BlockInputCoercion.Int(inputs, "TimeoutSeconds") ?? 30, MaxTimeoutSeconds, "TimeoutSeconds");
        var throwOnFailure = BlockInputCoercion.Boolean(inputs, "ThrowOnFailure") ?? false;

        var httpMethod = method.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "PATCH" => HttpMethod.Patch,
            _ => throw new ArgumentException($"Unsupported HTTP method: '{method}'.")
        };

        var headers = ReadFields(await RevealedAsync(inputs, "Headers", context, cancellationToken).ConfigureAwait(false), "Headers");
        var lease = ResolveHttpClient(context);

        try
        {
            try
            {
                ValidateUrl(url, context);

                using var request = new HttpRequestMessage(httpMethod, url);
                if (body is not null)
                    request.Content = new StringContent(body, Encoding.UTF8);

                ApplyFields(request, headers);

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                using var response = await lease.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                var responseBody = await ReadBodyAsync(response.Content, MaxResponseBodyBytes, deadline.Token).ConfigureAwait(false);

                if (throwOnFailure && !response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP request failed with status {(int)response.StatusCode}: {Excerpt(responseBody)}");
                }

                return new Dictionary<string, object?>
                {
                    ["StatusCode"] = (long)response.StatusCode,
                    ["Body"] = responseBody,
                    ["Success"] = response.IsSuccessStatusCode
                };
            }
            catch (Exception ex) when (IsEgressBlocked(ex))
            {
                if (throwOnFailure)
                    throw new HttpRequestException($"Request blocked: {ex.Message}", ex);

                return new Dictionary<string, object?>
                {
                    ["StatusCode"] = 0L,
                    ["Body"] = $"Request blocked: {ex.Message}",
                    ["Success"] = false
                };
            }
            catch (UriFormatException ex)
            {
                if (throwOnFailure)
                    throw new HttpRequestException($"Invalid URL: {ex.Message}", ex);

                return new Dictionary<string, object?>
                {
                    ["StatusCode"] = 0L,
                    ["Body"] = $"Invalid URL: {ex.Message}",
                    ["Success"] = false
                };
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("request URI", StringComparison.OrdinalIgnoreCase))
            {
                if (throwOnFailure)
                    throw new HttpRequestException($"Invalid URL: {ex.Message}", ex);

                return new Dictionary<string, object?>
                {
                    ["StatusCode"] = 0L,
                    ["Body"] = $"Invalid URL: {ex.Message}",
                    ["Success"] = false
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (throwOnFailure)
                    throw new HttpRequestException("Request timed out.");

                return new Dictionary<string, object?>
                {
                    ["StatusCode"] = 0L,
                    ["Body"] = "Request timed out.",
                    ["Success"] = false
                };
            }
            catch (HttpRequestException ex)
            {
                if (throwOnFailure)
                    throw;

                return new Dictionary<string, object?>
                {
                    ["StatusCode"] = 0L,
                    ["Body"] = ex.Message,
                    ["Success"] = false
                };
            }
        }
        finally
        {
            if (lease.CallerOwnsClient)
                lease.Client.Dispose();
        }
    }

    private static async Task<string> ReadBodyAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw BodyTooLarge(maxBytes);

        using var buffer = new MemoryStream();
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw BodyTooLarge(maxBytes);
                buffer.Write(chunk, 0, read);
            }
        }
        catch (IOException ex)
        {
            throw new HttpRequestException($"The response body could not be read: {ex.Message}", ex);
        }

        using var buffered = new ByteArrayContent(buffer.GetBuffer(), 0, (int)buffer.Length);
        buffered.Headers.ContentType = DecodableAs(content.Headers.ContentType);
        return await buffered.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MediaTypeHeaderValue? DecodableAs(MediaTypeHeaderValue? contentType)
    {
        if (contentType?.CharSet is not { } charSet)
            return contentType;

        try
        {
            Encoding.GetEncoding(charSet.Trim('"'));
            return contentType;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static HttpRequestException BodyTooLarge(long maxBytes) =>
        new($"The response body is larger than {maxBytes} bytes, the most the block reads.");

    private static string Excerpt(string body)
    {
        if (body.Length <= MaxQuotedBodyCharacters)
            return body;
        var cut = char.IsHighSurrogate(body[MaxQuotedBodyCharacters - 1]) ? MaxQuotedBodyCharacters - 1 : MaxQuotedBodyCharacters;
        return $"{body[..cut]}... ({body.Length} characters)";
    }

    private readonly record struct Field(string Name, string Value)
    {
        public override string ToString() => $"Field {{ Name = {Name} }}";
    }

    private static void ApplyFields(HttpRequestMessage request, List<Field> fields)
    {
        MediaTypeHeaderValue? mediaType = null;
        foreach (var field in fields)
        {
            if (string.Equals(field.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                RequireBody(request, field);
                mediaType = MediaTypeHeaderValue.TryParse(field.Value, out var parsed)
                    ? parsed
                    : throw new ArgumentException($"Input 'Headers' key {Quoted(field.Name)} has a Content-Type that is not a media type.");
                if (parsed.CharSet is not null && !string.Equals(parsed.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Input 'Headers' key {Quoted(field.Name)} has a Content-Type with a charset other than utf-8, which is how the block encodes the Body.");
                continue;
            }

            if (request.Headers.TryAddWithoutValidation(field.Name, field.Value))
                continue;
            RequireBody(request, field);
            if (!request.Content!.Headers.TryAddWithoutValidation(field.Name, field.Value))
                throw new ArgumentException($"Input 'Headers' key {Quoted(field.Name)} cannot be sent as a header.");
        }

        if (request.Content is not null)
            request.Content.Headers.ContentType = mediaType ?? new MediaTypeHeaderValue("application/json");
    }

    private static void RequireBody(HttpRequestMessage request, Field field)
    {
        if (request.Content is null)
            throw new ArgumentException($"Input 'Headers' key {Quoted(field.Name)} is a header of the body, and the request has no Body.");
    }

    private static async ValueTask<IReadOnlyDictionary<string, string>?> RevealedAsync(
        IReadOnlyDictionary<string, object?> inputs, string key, WorkflowBlockContext context, CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue(key, out var raw) || raw is null)
            return null;
        if (raw is not IReadOnlyDictionary<string, string> entries)
            throw new ArgumentException($"Input '{key}' must be a map of field names to values.");
        return await context.Secrets.RevealAsync(key, entries, cancellationToken).ConfigureAwait(false);
    }

    private static List<Field> ReadFields(IReadOnlyDictionary<string, string>? entries, string key)
    {
        var fields = new List<Field>();
        if (entries is null)
            return fields;

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var row = 0;
        foreach (var (name, text) in entries)
        {
            row++;
            var notToken = FieldNames.FirstNonToken(name);
            if (notToken >= 0)
                throw new ArgumentException(NotAFieldName(key, name, notToken, row));
            if (TransportFields.Contains(name))
                throw new ArgumentException($"Input '{key}' key {Quoted(name)} is a field the block takes from the Url, the Body or the connection, so an entry cannot set it.");
            if (text is null)
                throw new ArgumentException($"Input '{key}' key {Quoted(name)} has no value.");
            var value = text.Trim(FieldWhiteSpace);
            if (!IsFieldValue(value))
                throw new ArgumentException($"Input '{key}' key {Quoted(name)} has a value with a character other than visible ASCII, a space or a tab, which a field value cannot hold.");
            if (seen.TryGetValue(name, out var first))
                throw new ArgumentException($"Input '{key}' keys {Quoted(first)} and {Quoted(name)} name one field; a field is sent once or refused.");
            seen[name] = name;
            fields.Add(new Field(name, value));
        }

        return fields;
    }

    private static readonly HashSet<string> TransportFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Transfer-Encoding", "Host", "Connection", "Upgrade", "Keep-Alive", "Proxy-Connection", "TE",
        "Content-Encoding", "Accept-Encoding", "Expect", "Trailer",
    };

    private static string NotAFieldName(string key, string name, int at, int row) =>
        name.Length == 0
            ? $"Input '{key}' row {row} has an empty key; a name is a token, which is at least one character."
        : at == 0
            ? $"Input '{key}' row {row} has a key that is not a field name; a name is a token, and its character 1 is not a token character."
            : $"Input '{key}' key '{name[..at]}...' is not a field name; a name is a token, and its character {at + 1} is not a token character.";

    private static string Quoted(string name) => $"'{name}'";

    private static readonly char[] FieldWhiteSpace = [' ', '\t'];

    private static bool IsFieldValue(string value) => value.All(c => c == '\t' || (c >= ' ' && c <= '~'));
}

/// <summary>Registers the standard <c>HttpRequest</c> block. Derive and override <see cref="Create"/> to put a
/// subclass of <see cref="HttpRequestBlock"/> behind the same id — that is what an SSRF guard hangs off.</summary>
/// <remarks><c>Headers</c> is masked by header name, which <see cref="ReeveWorkflow.Core.Hosting.WorkflowSensitiveNames"/> does not match for
/// <c>Ocp-Apim-Subscription-Key</c>, <c>X-Hub-Signature</c>, <c>X-Auth</c> or a bare <c>Key</c>; protect an entry under such a
/// name through the editor's hide toggle, or its value is shown as written.</remarks>
public class HttpRequestBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "HttpRequest";
    public string DisplayName => "HTTP Request";
    public string Description => "Sends an HTTP request to a URL and returns the status code, body, and success flag.";
    public string Category => "Network";
    /// <summary>The method is an input, so this may be a POST that charges twice; turn retry on per node when the
    /// endpoint is known to be safe to repeat.</summary>
    public bool IsIdempotent => false;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Url", "URL", PortValueType.String, true),
        new BlockPortDefinition("Method", "Method", PortValueType.String, false,
            AllowedValues: ["GET", "POST", "PUT", "DELETE", "PATCH"]),
        new BlockPortDefinition("Headers", "Headers", PortValueType.MapOf(PortValueType.String), false) { SecretValues = true },
        new BlockPortDefinition("Body", "Body", PortValueType.String, false),
        new BlockPortDefinition("TimeoutSeconds", "Timeout (s)", PortValueType.Int, false),
        new BlockPortDefinition("ThrowOnFailure", "Throw On Failure", PortValueType.Boolean, false)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("StatusCode", "Status Code", PortValueType.Int, false),
        new BlockPortDefinition("Body", "Response Body", PortValueType.String, false),
        new BlockPortDefinition("Success", "Success", PortValueType.Boolean, false)
    ];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("Url", "URL", ConfigFieldType.String,
            Required: false, Description: "The URL to send the request to."),
        new BlockConfigFieldDefinition("Method", "Method", ConfigFieldType.String,
            Required: false, DefaultValue: "GET",
            Description: "HTTP method: GET, POST, PUT, DELETE, PATCH."),
        new BlockConfigFieldDefinition("Body", "Request Body", ConfigFieldType.String,
            Required: false, Description: "Optional request body (sent as application/json)."),
        new BlockConfigFieldDefinition("TimeoutSeconds", "Timeout (seconds)", ConfigFieldType.Int,
            Required: false, DefaultValue: "30",
            Description: "Request timeout in seconds."),
        new BlockConfigFieldDefinition("ThrowOnFailure", "Throw on failure", ConfigFieldType.Boolean,
            Required: false, DefaultValue: "false",
            Description: "Throw an exception for network errors, timeouts, invalid URLs, or non-2xx status codes so workflow error policies can handle the failure.")
    ];

    public virtual IWorkflowBlockPlugin Create() => new HttpRequestBlock();
}
