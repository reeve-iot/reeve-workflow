using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Dispatch.Host.Engine;

internal sealed class PostgresKeyRing(string connectionString) : IXmlRepository
{
    private const string Table = WorkflowSchemaMigrationService.KeyRingTable;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"SELECT xml FROM {Table} ORDER BY id", connection);
        using var reader = command.ExecuteReader();
        var keys = new List<XElement>();
        while (reader.Read())
            keys.Add(XElement.Parse(reader.GetString(0)));
        return keys;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"INSERT INTO {Table} (friendly_name, xml) VALUES (@name, @xml)", connection);
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}
