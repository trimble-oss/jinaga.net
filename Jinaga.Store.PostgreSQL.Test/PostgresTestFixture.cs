using Jinaga.Store.PostgreSQL;
using Jinaga.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Collections.Immutable;

namespace Jinaga.Store.PostgreSQL.Test;

public class PostgresTestFixture : IDisposable
{
    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("JINAGA_POSTGRES_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=jinaga_test;Username=jinaga;Password=jinaga_test";

    private readonly List<string> createdDatabases = new();

    public string CreateTestDatabase()
    {
        var dbName = "jinaga_test_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        using (var conn = new NpgsqlConnection(BaseConnectionString))
        {
            conn.Open();
            using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        var builder = new NpgsqlConnectionStringBuilder(BaseConnectionString);
        builder.Database = dbName;
        createdDatabases.Add(dbName);
        return builder.ConnectionString;
    }

    public PostgreSQLStore CreateStore()
    {
        return new PostgreSQLStore(CreateTestDatabase(), NullLoggerFactory.Instance);
    }

    public JinagaClient CreateJinagaClient()
    {
        var store = CreateStore();
        return new JinagaClient(
            store,
            new Jinaga.DefaultImplementations.LocalNetwork(),
            ImmutableList<Specification>.Empty,
            NullLoggerFactory.Instance,
            new JinagaClientOptions()
        );
    }

    public void Dispose()
    {
        try
        {
            NpgsqlConnection.ClearAllPools();

            using (var conn = new NpgsqlConnection(BaseConnectionString))
            {
                conn.Open();
                foreach (var dbName in createdDatabases)
                {
                    try
                    {
                        using (var cmd = new NpgsqlCommand(
                            $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", conn))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch
                    {
                        // Best effort cleanup
                    }
                }
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
