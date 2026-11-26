using System.Collections;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace ReeveWorkflow.AspNetCore.Tests;

public static class WorkflowApiSurface
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record EndpointDescription(
        string Operation,
        string Method,
        string Path,
        IReadOnlyList<ParameterDescription> PathParameters,
        IReadOnlyList<ParameterDescription> QueryParameters,
        BodyDescription? RequestBody,
        IReadOnlyList<ResponseDescription> Responses);

    public sealed record ParameterDescription(string Name, string Type, bool Required);

    public sealed record BodyDescription(string Type, bool Required);

    public sealed record ResponseDescription(int Status, string? Type);

    public sealed record PropertyDescription(string Name, string Type);

    public sealed record TypeDescription(string Name, IReadOnlyList<PropertyDescription> Properties);

    public sealed record Surface(
        int FormatVersion,
        string Source,
        IReadOnlyList<EndpointDescription> Endpoints,
        IReadOnlyList<TypeDescription> Types);

    public static Surface Describe(IEnumerable<Endpoint> endpoints, params string[] pathPrefixes) =>
        Describe("ReeveWorkflow.AspNetCore.WorkflowApiEndpoints", endpoints, pathPrefixes);

    public static Surface Describe(string source, IEnumerable<Endpoint> endpoints, params string[] pathPrefixes)
    {
        var closure = new SortedDictionary<string, TypeDescription>(StringComparer.Ordinal);
        var pending = new Queue<Type>();

        var described = endpoints
            .OfType<RouteEndpoint>()
            .Select(e => new { Endpoint = e, Path = (e.RoutePattern.RawText ?? string.Empty).TrimStart('/') })
            .Where(e => pathPrefixes.Any(p => e.Path.StartsWith(p, StringComparison.Ordinal)))
            .Select(e => Describe(e.Endpoint, e.Path, pending))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ToList();

        var collision = described.GroupBy(e => e.Operation, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (collision is not null)
            throw new InvalidOperationException(
                $"{collision.Key} names {collision.Count()} endpoints: "
                + string.Join(", ", collision.Select(e => $"{e.Method} {e.Path}")) + ".");

        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            var name = TypeName(type);
            if (closure.ContainsKey(name))
                continue;
            closure[name] = DescribeType(type, pending);
        }

        return new Surface(FormatVersion, source, described, closure.Values.ToList());
    }

    public static string ToJson(Surface surface) => JsonSerializer.Serialize(surface, Canonical) + "\n";

    private static EndpointDescription Describe(RouteEndpoint endpoint, string path, Queue<Type> pending)
    {
        var method = string.Join('|',
            (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Order(StringComparer.Ordinal));

        var handler = endpoint.Metadata.GetMetadata<MethodInfo>()
            ?? throw new InvalidOperationException($"{method} {path} carries no handler method in its metadata.");

        var pathParameterNames = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var nullability = new NullabilityInfoContext();
        var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();

        var pathParameters = new List<ParameterDescription>();
        var queryParameters = new List<ParameterDescription>();
        foreach (var parameter in handler.GetParameters())
        {
            if (IsAmbient(parameter) || IsTheBody(parameter, accepts))
                continue;

            var name = parameter.Name ?? throw new InvalidOperationException($"{method} {path} has an unnamed parameter.");
            var info = nullability.Create(parameter);
            var described = new ParameterDescription(
                name, Describe(parameter.ParameterType, info, pending),
                Required: !parameter.HasDefaultValue && info.ReadState != NullabilityState.Nullable);

            if (pathParameterNames.Contains(name))
                pathParameters.Add(described);
            else
                queryParameters.Add(described);
        }

        var body = accepts?.RequestType is { } requestType
            ? new BodyDescription(Describe(requestType, null, pending), Required: !accepts.IsOptional)
            : null;

        var responses = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Select(r => new ResponseDescription(
                r.StatusCode,
                r.Type is null || r.Type == typeof(void) ? null : Describe(r.Type, null, pending)))
            .GroupBy(r => r.Status)
            .Select(g => OnlyShapeOf(g, method, path))
            .OrderBy(r => r.Status)
            .ToList();

        return new EndpointDescription(
            OperationName(handler), method, path,
            pathParameters.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(),
            queryParameters.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(),
            body, responses);
    }

    private static ResponseDescription OnlyShapeOf(
        IGrouping<int, ResponseDescription> declared, string method, string path)
    {
        var distinct = declared.DistinctBy(r => r.Type, StringComparer.Ordinal).ToList();
        if (distinct.Count == 1)
            return distinct[0];
        throw new InvalidOperationException(
            $"{method} {path} declares {declared.Key} as "
            + string.Join(" and ", distinct.Select(r => r.Type ?? "nothing")) + ".");
    }

    private static bool IsTheBody(ParameterInfo parameter, IAcceptsMetadata? accepts) =>
        accepts?.RequestType is { } requestType
        && (parameter.ParameterType == requestType
            || Nullable.GetUnderlyingType(parameter.ParameterType) == requestType);

    private static bool IsAmbient(ParameterInfo parameter) =>
        parameter.ParameterType == typeof(ClaimsPrincipal)
        || parameter.ParameterType == typeof(CancellationToken)
        || parameter.ParameterType == typeof(HttpContext)
        || parameter.GetCustomAttribute<FromServicesAttribute>() is not null
        || parameter.GetCustomAttribute<FromBodyAttribute>() is not null;

    private static string OperationName(MethodInfo handler)
    {
        var name = handler.Name;
        if (name.EndsWith("Async", StringComparison.Ordinal))
            name = name[..^"Async".Length];
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static TypeDescription DescribeType(Type type, Queue<Type> pending)
    {
        var nullability = new NullabilityInfoContext();
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .Select(p => new PropertyDescription(
                p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? char.ToLowerInvariant(p.Name[0]) + p.Name[1..],
                Describe(p.PropertyType, nullability.Create(p), pending)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        return new TypeDescription(TypeName(type), properties);
    }

    private static string Describe(Type type, NullabilityInfo? nullability, Queue<Type> pending)
    {
        var suffix = nullability?.ReadState == NullabilityState.Nullable ? "?" : string.Empty;

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return Describe(underlying, null, pending) + "?";

        if (Scalar(type) is { } scalar)
            return scalar + suffix;

        if (DictionaryValue(type) is { } valueType)
            return $"map<{Describe(valueType, nullability?.GenericTypeArguments.ElementAtOrDefault(1), pending)}>" + suffix;

        if (ElementOf(type) is { } elementType)
            return $"{Describe(elementType, nullability?.ElementType ?? nullability?.GenericTypeArguments.FirstOrDefault(), pending)}[]" + suffix;

        pending.Enqueue(type);
        return "#" + TypeName(type) + suffix;
    }

    private static readonly Type ExactNameEnumConverterDefinition =
        typeof(ReeveWorkflow.Core.Hosting.WorkflowDefinitionOrigin).Assembly.GetType("ReeveWorkflow.Core.Hosting.ExactNameEnumConverter`1", throwOnError: true)!;

    private static string? Scalar(Type type) => type switch
    {
        _ when type == typeof(string) => "string",
        _ when type == typeof(Guid) => "uuid",
        _ when type == typeof(DateTimeOffset) || type == typeof(DateTime) => "date-time",
        _ when type == typeof(int) || type == typeof(long) || type == typeof(short) => "integer",
        _ when type == typeof(double) || type == typeof(float) || type == typeof(decimal) => "number",
        _ when type == typeof(bool) => "boolean",
        _ when type == typeof(object) => "unknown",
        _ when type.IsEnum => type.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType is { IsGenericType: true } converter
                              && (converter.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>)
                                  || converter.GetGenericTypeDefinition() == ExactNameEnumConverterDefinition)
            ? "string"
            : throw new InvalidOperationException(
                $"{type.Name} is an enum on the wire, and whether it serialises as its name or its ordinal "
                + "depends on a converter this reader cannot see. Declare JsonStringEnumConverter<T> (or the library's ExactNameEnumConverter<T>) on the enum, and it is a string."),
        _ => null,
    };

    private static Type? DictionaryValue(Type type) =>
        type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType
                                 && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                                     || i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
            ?.GetGenericArguments()[1];

    private static Type? ElementOf(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        return type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private static string TypeName(Type type) =>
        typeof(IEnumerable).IsAssignableFrom(type) || type.IsGenericType
            ? throw new InvalidOperationException($"{type} reached the closure as a shape; it should have been described structurally.")
            : type.Name;
}
