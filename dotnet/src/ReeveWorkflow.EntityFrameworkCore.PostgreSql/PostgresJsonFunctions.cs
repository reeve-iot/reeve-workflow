using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace ReeveWorkflow.EntityFrameworkCore.PostgreSql;

/// <summary>PostgreSQL's built-in jsonb functions, the ones behind <c>-</c>, <c>||</c>, <c>#-</c>, <c>-&gt;</c>, <c>-&gt;&gt;</c>
/// and <c>@&gt;</c>, as methods a LINQ query can call. A query that edits a jsonb column with them (inside
/// <c>ExecuteUpdate</c>) stays one statement in the database; nothing is read and written back in C#. Every method
/// translates only inside a query and throws when called directly.</summary>
/// <remarks>
/// A <see cref="string"/> overload takes a jsonb column mapped as <c>string</c>, such as <c>Workflow.ImportWarningsJson</c>.
/// An <see cref="IReadOnlyDictionary{TKey, TValue}"/> overload takes <c>Workflow.HostMetadata</c> and only deletes or reads,
/// so a stored value stays one its converter reads back; a <see cref="JsonDocument"/> overload deletes on a
/// <c>JsonDocument</c> column. PostgreSQL's functions are strict: a null column gives null, so a nullable column gives a nullable result.
/// <see cref="JsonbObjectField"/>, <see cref="JsonbObjectFieldText"/> and <see cref="NullIfEmptyObject(string)"/> can also return null for
/// arguments that are not (a missing key, a JSON null, an emptied object), and their results are nullable whatever the column is, so
/// <c>== null</c> and <c>!= null</c> against them render as <c>IS NULL</c> and <c>IS NOT NULL</c>.
/// Registered in every model built with <c>UseWorkflowPostgreSqlSchema</c>; a context
/// of the host's own calls <see cref="PostgresJsonModelBuilderExtensions.AddPostgresJsonFunctions"/>.
/// Do not read <c>JsonElement.ValueKind</c> in a query: Npgsql translates it to the wrong SQL without an error.
/// </remarks>
public static class PostgresJsonFunctions
{
    private const string Jsonb = "jsonb";
    private const string Text = "text";
    private const string TextArray = "text[]";

    /// <summary><c>json - key</c>: the object without <paramref name="key"/>, or the array without that string element.</summary>
    [return: NotNullIfNotNull(nameof(json))]
    public static string? JsonbDelete(string? json, string key) => throw Direct();

    /// <inheritdoc cref="JsonbDelete(string, string)"/>
    /// <remarks>Pair it with <see cref="NullIfEmptyObject(IReadOnlyDictionary{string, string})"/>: an empty map is stored as null.</remarks>
    [return: NotNullIfNotNull(nameof(json))]
    public static IReadOnlyDictionary<string, string>? JsonbDelete(IReadOnlyDictionary<string, string>? json, string key) => throw Direct();

    /// <inheritdoc cref="JsonbDelete(string, string)"/>
    [return: NotNullIfNotNull(nameof(json))]
    public static JsonDocument? JsonbDelete(JsonDocument? json, string key) => throw Direct();

    /// <summary><c>json || patch</c>: the keys of <paramref name="patch"/> (jsonb text) laid over <paramref name="json"/>.</summary>
    [return: NotNullIfNotNull(nameof(json))]
    public static string? JsonbConcat(string? json, string patch) => throw Direct();

    /// <summary><c>json #- path</c>: the document without the value at <paramref name="path"/>.</summary>
    [return: NotNullIfNotNull(nameof(json))]
    public static string? JsonbDeletePath(string? json, string[] path) => throw Direct();

    /// <summary><c>jsonb_set(json, path, value)</c>: <paramref name="value"/> (jsonb text, so a JSON string needs its quotes)
    /// placed at <paramref name="path"/>. A null value or path makes the result null, which an update stores over the whole column.</summary>
    [return: NotNullIfNotNull(nameof(json))]
    public static string? JsonbSet(string? json, string[] path, string value) => throw Direct();

    /// <summary><c>json -&gt; key</c>: the member as jsonb; null for a missing key or a document that is not an object.</summary>
    public static string? JsonbObjectField(string? json, string key) => throw Direct();

    /// <summary><c>json -&gt;&gt; key</c>: the member as text, a JSON string without its quotes; null when missing or JSON null.</summary>
    public static string? JsonbObjectFieldText(string? json, string key) => throw Direct();

    /// <inheritdoc cref="JsonbObjectFieldText(string, string)"/>
    public static string? JsonbObjectFieldText(IReadOnlyDictionary<string, string>? json, string key) => throw Direct();

    /// <summary><c>json @&gt; contained</c>: whether <paramref name="json"/> contains <paramref name="contained"/> (jsonb text).
    /// For a <c>Where</c>: over a null column the answer is null, which a filter reads as false.</summary>
    public static bool JsonbContains(string? json, string contained) => throw Direct();

    /// <inheritdoc cref="JsonbContains(string, string)"/>
    public static bool JsonbContains(IReadOnlyDictionary<string, string>? json, string contained) => throw Direct();

    /// <summary><c>jsonb_typeof(json)</c>: <c>object</c>, <c>array</c>, <c>string</c>, <c>number</c>, <c>boolean</c> or <c>null</c>.</summary>
    [return: NotNullIfNotNull(nameof(json))]
    public static string? JsonbTypeof(string? json) => throw Direct();

    /// <inheritdoc cref="JsonbTypeof(string)"/>
    [return: NotNullIfNotNull(nameof(json))]
    public static string? JsonbTypeof(IReadOnlyDictionary<string, string>? json) => throw Direct();

    /// <summary><c>to_jsonb(text)</c>, with the argument cast to text unless it is text already (a string parameter is sent as text), so a literal or a parameter has a type: <paramref name="text"/> as a JSON string, for <see cref="JsonbContains(string, string)"/> against an array of strings.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? ToJsonb(string? text) => throw Direct();

    /// <summary><c>nullif(json, '{}'::jsonb)</c>: null for an empty object, so an edit that removed the last key stores no document.</summary>
    public static string? NullIfEmptyObject(string? json) => throw Direct();

    /// <inheritdoc cref="NullIfEmptyObject(string)"/>
    public static IReadOnlyDictionary<string, string>? NullIfEmptyObject(IReadOnlyDictionary<string, string>? json) => throw Direct();

    /// <summary><c>json::text</c>, for <c>EF.Functions.ILike</c> over a jsonb column.</summary>
    [return: NotNullIfNotNull(nameof(json))]
    public static string? ToText(string? json) => throw Direct();

    private static NotSupportedException Direct() => new(
        "A PostgresJsonFunctions method translates to SQL only inside a query; it cannot be called directly.");

    private static readonly Type Dictionary = typeof(IReadOnlyDictionary<string, string>);

    private static MethodInfo Method(string name, params Type[] parameters) =>
        typeof(PostgresJsonFunctions).GetMethod(name, parameters)!;

    internal static void AddTo(IMutableModel model)
    {
        Map(model, Method(nameof(JsonbDelete), typeof(string), typeof(string)), "jsonb_delete", Result.Json, Arg.Jsonb, Arg.Text);
        Map(model, Method(nameof(JsonbDelete), typeof(JsonDocument), typeof(string)), "jsonb_delete", Result.Json, Arg.Jsonb, Arg.Text);
        Map(model, Method(nameof(JsonbDelete), Dictionary, typeof(string)), "jsonb_delete", Result.Json, Arg.Jsonb, Arg.Text);
        Map(model, Method(nameof(JsonbConcat), typeof(string), typeof(string)), "jsonb_concat", Result.Json, Arg.Jsonb, Arg.Jsonb);
        Map(model, Method(nameof(JsonbDeletePath), typeof(string), typeof(string[])), "jsonb_delete_path", Result.Json, Arg.Jsonb, Arg.TextArray);
        Map(model, Method(nameof(JsonbSet), typeof(string), typeof(string[]), typeof(string)), "jsonb_set", Result.Json, Arg.Jsonb, Arg.TextArray, Arg.Jsonb);
        MapNullable(model, Method(nameof(JsonbObjectField), typeof(string), typeof(string)), "jsonb_object_field", Result.Json, Arg.Jsonb, Arg.Text);

        foreach (var json in new[] { typeof(string), Dictionary })
        {
            MapNullable(model, Method(nameof(JsonbObjectFieldText), json, typeof(string)), "jsonb_object_field_text", Result.Text, Arg.Jsonb, Arg.Text);
            Map(model, Method(nameof(JsonbContains), json, typeof(string)), "jsonb_contains", Result.Bool, Arg.Jsonb, Arg.Jsonb);
            Map(model, Method(nameof(JsonbTypeof), json), "jsonb_typeof", Result.Text, Arg.Jsonb);
            AddNullIfEmptyObject(model, Method(nameof(NullIfEmptyObject), json), json);
        }

        Add(model, Method(nameof(ToJsonb), typeof(string)), function =>
        {
            function.Parameters[0].StoreType = Text;
            function.StoreType = Jsonb;
            function.Translation = arguments => new SqlFunctionExpression(
                "to_jsonb", [new SqlUnaryExpression(System.Linq.Expressions.ExpressionType.Convert, arguments[0], typeof(string), TextMapping)],
                nullable: true, argumentsPropagateNullability: [true], typeof(string), JsonbMapping);
        });

        Add(model, Method(nameof(ToText), typeof(string)), function =>
        {
            function.Parameters[0].StoreType = Jsonb;
            function.Translation = arguments => new SqlUnaryExpression(
                System.Linq.Expressions.ExpressionType.Convert, AsJsonb(arguments[0]), typeof(string), TextMapping);
        });
    }

    private enum Arg { Jsonb, Text, TextArray }

    private enum Result { Json, Text, Bool }

    private static readonly StringTypeMapping TextMapping = new(Text, DbType.String);

    private static readonly StringTypeMapping JsonbMapping = new(Jsonb, DbType.String);

    private static SqlExpression AsJsonb(SqlExpression argument) =>
        argument.TypeMapping?.StoreType == Jsonb
            ? argument
            : new SqlUnaryExpression(System.Linq.Expressions.ExpressionType.Convert, argument, typeof(string), JsonbMapping);

    private static void Map(IMutableModel model, MethodInfo method, string name, Result returns, params Arg[] parameters) =>
        Declare(model, method, name, returns, parameters, absentIsNull: false);

    private static void MapNullable(IMutableModel model, MethodInfo method, string name, Result returns, params Arg[] parameters) =>
        Declare(model, method, name, returns, parameters, absentIsNull: true);

    private static void Declare(IMutableModel model, MethodInfo method, string name, Result returns, Arg[] parameters, bool absentIsNull) =>
        Add(model, method, function =>
        {
            function.StoreType = returns switch { Result.Json => Jsonb, Result.Text => Text, _ => null };
            for (var index = 0; index < parameters.Length; index++)
                function.Parameters[index].StoreType = parameters[index] switch { Arg.Jsonb => Jsonb, Arg.Text => Text, _ => TextArray };

            function.Translation = arguments =>
            {
                var converted = new SqlExpression[arguments.Count];
                for (var index = 0; index < converted.Length; index++)
                    converted[index] = parameters[index] == Arg.Jsonb ? AsJsonb(arguments[index]) : arguments[index];

                RelationalTypeMapping mapping = returns switch { Result.Json => converted[0].TypeMapping ?? JsonbMapping, Result.Text => TextMapping, _ => BoolMapping };
                return new SqlFunctionExpression(name, converted, nullable: true,
                    argumentsPropagateNullability: Enumerable.Repeat(!absentIsNull, converted.Length), method.ReturnType, mapping);
            };
        });

    private static readonly BoolTypeMapping BoolMapping = new("boolean", DbType.Boolean);

    private static void AddNullIfEmptyObject(IMutableModel model, MethodInfo method, Type returns) =>
        Add(model, method, function =>
        {
            function.Parameters[0].StoreType = Jsonb;
            function.StoreType = Jsonb;
            function.Translation = arguments => new SqlFunctionExpression(
                "nullif", [AsJsonb(arguments[0]), new SqlFragmentExpression("'{}'::jsonb")], nullable: true,
                argumentsPropagateNullability: [false, false], returns, arguments[0].TypeMapping ?? JsonbMapping);
        });

    private static void Add(IMutableModel model, MethodInfo method, Action<IMutableDbFunction> configure)
    {
        if (model.FindDbFunction(method) is not null)
            return;

        configure(model.AddDbFunction(method));
    }
}

/// <summary>Registers <see cref="PostgresJsonFunctions"/> in a model the workflow library does not build.</summary>
public static class PostgresJsonModelBuilderExtensions
{
    /// <summary>Makes <see cref="PostgresJsonFunctions"/> callable from queries over this model. Call it in
    /// <c>OnModelCreating</c>; calling it twice, or in a model that already has them, changes nothing.</summary>
    public static ModelBuilder AddPostgresJsonFunctions(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        PostgresJsonFunctions.AddTo(modelBuilder.Model);
        return modelBuilder;
    }
}
