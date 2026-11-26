using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;
using static ReeveWorkflow.EntityFrameworkCore.PostgreSql.PostgresJsonFunctions;

namespace ReeveWorkflow.EntityFrameworkCore.PostgreSql.Tests;

public sealed class PostgresJsonFunctionsTranslationTests
{
    private static WorkflowDbContext Context() => new(new DbContextOptionsBuilder<WorkflowDbContext>()
        .UseNpgsql("Host=workflow-test.invalid").UseWorkflowPostgreSqlSchema().Options);

    private static string Sql<T>(Func<WorkflowDbContext, IQueryable<T>> query)
    {
        using var context = Context();
        return query(context).ToQueryString();
    }

    private static string OnString<T>(System.Linq.Expressions.Expression<Func<Workflow, T>> select)
    {
        using var context = Context();
        return context.Workflows.Select(select).ToQueryString();
    }

    [Fact]
    public void Delete_on_a_string_column_is_jsonb_delete_for_a_literal_and_for_a_captured_key()
    {
        var key = "a";

        OnString(w => JsonbDelete(w.ImportWarningsJson, "a")).Should().Contain("jsonb_delete(w.import_warnings_json, 'a')");
        OnString(w => JsonbDelete(w.ImportWarningsJson, key)).Should().Contain("jsonb_delete(w.import_warnings_json, @key)");
    }

    [Fact]
    public void Delete_on_host_metadata_is_jsonb_delete_over_the_converted_column()
    {
        var key = "folderId";

        OnString(w => JsonbDelete(w.HostMetadata, "folderId")).Should().Contain("jsonb_delete(w.host_metadata, 'folderId')");
        OnString(w => JsonbDelete(w.HostMetadata, key)).Should().Contain("jsonb_delete(w.host_metadata, @key)");
    }

    [Fact]
    public void Concat_casts_the_patch_to_jsonb_for_a_literal_and_for_a_captured_variable()
    {
        var patch = "{\"a\":1}";

        OnString(w => JsonbConcat(w.ImportWarningsJson, "{\"a\":1}")).Should().Contain("jsonb_concat(w.import_warnings_json, '{\"a\":1}'::jsonb)");
        OnString(w => JsonbConcat(w.ImportWarningsJson, patch)).Should().Contain("jsonb_concat(w.import_warnings_json, @patch::jsonb)");
    }

    [Fact]
    public void Delete_path_takes_a_text_array()
    {
        var path = new[] { "a", "b" };

        OnString(w => JsonbDeletePath(w.ImportWarningsJson, new[] { "a", "b" })).Should().Contain("jsonb_delete_path(w.import_warnings_json, ARRAY['a','b']::text[])");
        OnString(w => JsonbDeletePath(w.ImportWarningsJson, path)).Should().Contain("jsonb_delete_path(w.import_warnings_json, @path)");
    }

    [Fact]
    public void Set_takes_a_text_array_path_and_casts_the_value_to_jsonb()
    {
        var path = new[] { "a" };
        var value = "1";

        OnString(w => JsonbSet(w.ImportWarningsJson, new[] { "a" }, "1")).Should().Contain("jsonb_set(w.import_warnings_json, ARRAY['a']::text[], '1'::jsonb)");
        OnString(w => JsonbSet(w.ImportWarningsJson, path, value)).Should().Contain("jsonb_set(w.import_warnings_json, @path, @value::jsonb)");
    }

    [Fact]
    public void Object_field_is_jsonb_object_field_and_the_text_form_is_object_field_text()
    {
        var key = "a";

        OnString(w => JsonbObjectField(w.ImportWarningsJson, "a")).Should().Contain("jsonb_object_field(w.import_warnings_json, 'a')");
        OnString(w => JsonbObjectField(w.ImportWarningsJson, key)).Should().Contain("jsonb_object_field(w.import_warnings_json, @key)");
        OnString(w => JsonbObjectFieldText(w.ImportWarningsJson, key)).Should().Contain("jsonb_object_field_text(w.import_warnings_json, @key)");
        OnString(w => JsonbObjectFieldText(w.HostMetadata, "folderId")).Should().Contain("jsonb_object_field_text(w.host_metadata, 'folderId')");
    }

    [Fact]
    public void Object_field_text_works_as_a_predicate()
    {
        var folder = "f1";

        Sql(c => c.Workflows.Where(w => JsonbObjectFieldText(w.HostMetadata, "folderId") == "f1"))
            .Should().Contain("WHERE jsonb_object_field_text(w.host_metadata, 'folderId') = 'f1'");
        Sql(c => c.Workflows.Where(w => JsonbObjectFieldText(w.HostMetadata, "folderId") == folder))
            .Should().Contain("WHERE jsonb_object_field_text(w.host_metadata, 'folderId') = @folder");
    }

    [Fact]
    public void Contains_is_jsonb_contains_and_works_as_a_predicate()
    {
        var contained = "[1]";

        Sql(c => c.Workflows.Where(w => JsonbContains(w.ImportWarningsJson, "[1]")))
            .Should().Contain("WHERE jsonb_contains(w.import_warnings_json, '[1]'::jsonb)");
        Sql(c => c.Workflows.Where(w => JsonbContains(w.ImportWarningsJson, contained)))
            .Should().Contain("WHERE jsonb_contains(w.import_warnings_json, @contained::jsonb)");
        Sql(c => c.Workflows.Where(w => JsonbContains(w.HostMetadata, contained)))
            .Should().Contain("WHERE jsonb_contains(w.host_metadata, @contained::jsonb)");
    }

    [Fact]
    public void Typeof_is_jsonb_typeof_and_composes_with_object_field()
    {
        OnString(w => JsonbTypeof(w.ImportWarningsJson)).Should().Contain("jsonb_typeof(w.import_warnings_json)");
        OnString(w => JsonbTypeof(w.HostMetadata)).Should().Contain("jsonb_typeof(w.host_metadata)");
        OnString(w => JsonbTypeof(JsonbObjectField(w.ImportWarningsJson, "a")))
            .Should().Contain("jsonb_typeof(jsonb_object_field(w.import_warnings_json, 'a'))");
    }

    [Fact]
    public void To_jsonb_casts_a_literal_to_text_and_takes_a_text_parameter_as_it_is()
    {
        var id = "x";

        Sql(c => c.Workflows.Where(w => JsonbContains(JsonbObjectField(w.ImportWarningsJson, "ids"), ToJsonb("x"))))
            .Should().Contain("jsonb_contains(jsonb_object_field(w.import_warnings_json, 'ids'), to_jsonb('x'::text))");
        Sql(c => c.Workflows.Where(w => JsonbContains(JsonbObjectField(w.ImportWarningsJson, "ids"), ToJsonb(id))))
            .Should().Contain("jsonb_contains(jsonb_object_field(w.import_warnings_json, 'ids'), to_jsonb(@id))");
    }

    [Fact]
    public void Null_if_empty_object_carries_the_literal_in_the_function()
    {
        OnString(w => NullIfEmptyObject(JsonbDelete(w.ImportWarningsJson, "a")))
            .Should().Contain("nullif(jsonb_delete(w.import_warnings_json, 'a'), '{}'::jsonb)");
        OnString(w => NullIfEmptyObject(JsonbDelete(w.HostMetadata, "folderId")))
            .Should().Contain("nullif(jsonb_delete(w.host_metadata, 'folderId'), '{}'::jsonb)");
    }

    [Fact]
    public void To_text_casts_so_a_jsonb_column_can_meet_ilike()
    {
        using var context = Context();

        var sql = context.WorkflowVersions
            .Where(v => EF.Functions.ILike(ToText(v.DefinitionJson), "%abc%"))
            .Select(v => v.Id).ToQueryString();

        sql.Should().Contain("w.definition_json::text ILIKE '%abc%'");
    }

    [Fact]
    public async Task An_edit_through_ExecuteUpdate_is_one_update_whose_jsonb_arguments_are_cast_in_SQL()
    {
        var capture = new CaptureCommands();
        await using var context = new WorkflowDbContext(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql("Host=workflow-test.invalid").UseWorkflowPostgreSqlSchema().AddInterceptors(capture).Options);
        var folder = "f1";
        var key = "folderId";
        var path = new[] { "a" };
        var value = "1";
        var patch = "{\"b\":2}";

        await context.Workflows.Where(w => JsonbObjectFieldText(w.HostMetadata, key) == folder)
            .ExecuteUpdateAsync(set => set
                .SetProperty(w => w.HostMetadata, w => NullIfEmptyObject(JsonbDelete(w.HostMetadata, key)))
                .SetProperty(w => w.ImportWarningsJson, w => JsonbConcat(JsonbSet(w.ImportWarningsJson, path, value), patch)));

        var command = capture.Commands.Should().ContainSingle().Subject;
        command.Text.Should()
            .Contain("host_metadata = nullif(jsonb_delete(w.host_metadata, @key), '{}'::jsonb)")
            .And.Contain("import_warnings_json = jsonb_concat(jsonb_set(w.import_warnings_json, @path, @value::jsonb), @patch::jsonb)")
            .And.Contain("WHERE jsonb_object_field_text(w.host_metadata, @key) = @folder");
        command.Types.Should().Contain("key", NpgsqlDbType.Text).And.Contain("folder", NpgsqlDbType.Text)
            .And.Contain("value", NpgsqlDbType.Text).And.Contain("patch", NpgsqlDbType.Text)
            .And.Contain("path", NpgsqlDbType.Array | NpgsqlDbType.Text);
    }

    [Fact]
    public void Delete_on_a_JsonDocument_column_strips_keys_and_compares_with_the_column()
    {
        using var context = new HostContext();
        var first = "a";
        var second = "b";

        var sql = context.Rows.Where(r => JsonbDelete(JsonbDelete(r.Meta, first), second) != r.Meta).Select(r => r.Id).ToQueryString();

        sql.Should().Contain("jsonb_delete(jsonb_delete(r.meta, @first), @second)");
    }

    [Fact]
    public async Task Stripping_keys_from_a_JsonDocument_column_is_one_update()
    {
        var capture = new CaptureCommands();
        await using var context = new HostContext(capture);
        var first = "a";
        var second = "b";

        await context.Rows.Where(r => JsonbDelete(JsonbDelete(r.Meta, first), second) != r.Meta)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Meta, r => JsonbDelete(JsonbDelete(r.Meta, first), second)));

        capture.Commands.Should().ContainSingle().Which.Text.Should()
            .Contain("SET meta = jsonb_delete(jsonb_delete(r.meta, @first), @second)");
    }

    [Fact]
    public void Calling_a_function_outside_a_query_is_refused()
    {
        var call = () => JsonbDelete("{}", "a");

        call.Should().Throw<NotSupportedException>().WithMessage("*inside a query*");
    }

    [Fact]
    public void A_model_the_library_does_not_build_gets_the_functions_from_the_extension_and_may_ask_twice()
    {
        using var context = new HostContext();

        var sql = context.Rows.Select(r => NullIfEmptyObject(JsonbDelete(r.Data, "k"))).ToQueryString();

        sql.Should().Contain("nullif(jsonb_delete(r.data, 'k'), '{}'::jsonb)");
    }

    private static IEnumerable<(string Name, System.Linq.Expressions.Expression<Func<Row, string?>> Value)> CanReturnNullForNonNullArguments()
    {
        var key = "k";
        yield return ("JsonbObjectField, literal key", r => JsonbObjectField(r.Data, "k"));
        yield return ("JsonbObjectField, captured key", r => JsonbObjectField(r.Data, key));
        yield return ("JsonbObjectFieldText, literal key", r => JsonbObjectFieldText(r.Data, "k"));
        yield return ("JsonbObjectFieldText, captured key", r => JsonbObjectFieldText(r.Data, key));
        yield return ("JsonbObjectFieldText over a map", r => JsonbObjectFieldText(r.Map, "k"));
        yield return ("JsonbObjectFieldText over a map, captured key", r => JsonbObjectFieldText(r.Map, key));
        yield return ("NullIfEmptyObject", r => NullIfEmptyObject(r.Data));
        yield return ("NullIfEmptyObject over a delete", r => NullIfEmptyObject(JsonbDelete(r.Data, key)));
    }

    private static string WhereNull(System.Linq.Expressions.Expression<Func<Row, string?>> value, bool equal)
    {
        var nothing = System.Linq.Expressions.Expression.Constant(null, typeof(string));
        var compared = equal
            ? System.Linq.Expressions.Expression.Equal(value.Body, nothing)
            : System.Linq.Expressions.Expression.NotEqual(value.Body, nothing);
        var predicate = System.Linq.Expressions.Expression.Lambda<Func<Row, bool>>(compared, value.Parameters);
        using var context = new HostContext();
        return context.Rows.Where(predicate).Select(r => r.Id).ToQueryString();
    }

    public static TheoryData<string> NullableMappings() => new(CanReturnNullForNonNullArguments().Select(c => c.Name));

    [Theory]
    [MemberData(nameof(NullableMappings))]
    public void A_function_that_can_return_null_for_non_null_arguments_renders_null_comparisons_as_IS_NULL_and_IS_NOT_NULL(string name)
    {
        var value = CanReturnNullForNonNullArguments().Single(c => c.Name == name).Value;

        WhereNull(value, equal: true).Should().MatchRegex(@"WHERE .*\) IS NULL", "== null must not fold to FALSE");
        WhereNull(value, equal: false).Should().MatchRegex(@"WHERE .*\) IS NOT NULL", "!= null must not be dropped");
    }

    private sealed class CaptureCommands : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public List<Captured> Commands { get; } = [];

        ValueTask<InterceptionResult> IDbConnectionInterceptor.ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken) =>
            new(InterceptionResult.Suppress());

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(new Captured(command.CommandText, command.Parameters.OfType<NpgsqlParameter>()
                .ToDictionary(parameter => parameter.ParameterName.TrimStart('@'), parameter => parameter.NpgsqlDbType)));
            return new(InterceptionResult<int>.SuppressWithResult(1));
        }
    }

    private sealed record Captured(string Text, Dictionary<string, NpgsqlDbType> Types);

    private sealed class Row
    {
        public int Id { get; set; }
        public string Data { get; set; } = "";
        public IReadOnlyDictionary<string, string> Map { get; set; } = new Dictionary<string, string>();
        public JsonDocument? Meta { get; set; }
    }

    private sealed class HostContext(IInterceptor? interceptor = null) : DbContext
    {
        public DbSet<Row> Rows => Set<Row>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseNpgsql("Host=workflow-test.invalid");
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddPostgresJsonFunctions().AddPostgresJsonFunctions();
            modelBuilder.Entity<Row>().Property(r => r.Data).HasColumnName("data").HasColumnType("jsonb");
            modelBuilder.Entity<Row>().Property(r => r.Meta).HasColumnName("meta").HasColumnType("jsonb");
            modelBuilder.Entity<Row>().Property(r => r.Map).HasColumnName("map").HasColumnType("jsonb").HasConversion(
                map => JsonSerializer.Serialize(map, (JsonSerializerOptions?)null),
                text => JsonSerializer.Deserialize<Dictionary<string, string>>(text, (JsonSerializerOptions?)null)!);
        }
    }
}
