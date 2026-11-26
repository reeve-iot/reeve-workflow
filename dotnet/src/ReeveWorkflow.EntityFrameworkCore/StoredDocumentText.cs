namespace ReeveWorkflow.EntityFrameworkCore;

internal static class StoredDocumentText
{
    public static async Task<(string Definition, string? Inputs, string? Outputs)> AsStoredAsync(
        WorkflowDbContext db, string definition, string? inputs, string? outputs, CancellationToken cancellationToken)
    {
        if (!PostgresStatements.Applies(db.Database))
            return (definition, inputs, outputs);

        return await PostgresStatements.RunAsync(
                db.Database, "SELECT $1::jsonb::text, $2::jsonb::text, $3::jsonb::text", [definition, inputs, outputs],
                async (command, token) =>
                {
                    await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await reader.ReadAsync(token).ConfigureAwait(false);
                    return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
