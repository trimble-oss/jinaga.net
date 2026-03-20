using Jinaga.Store.PostgreSQL;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jinaga.Store.PostgreSQL.Test;

public class PostgresTestFixture : IDisposable
{
    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("JINAGA_POSTGRES_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=jinaga_test;Username=jinaga;Password=jinaga_test";

    public string DatabaseName { get; }
    public string ConnectionString { get; }

    public PostgresTestFixture()
    {
        DatabaseName = "jinaga_test_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // Create the test database
        using (var conn = new NpgsqlConnection(BaseConnectionString))
        {
            conn.Open();
            using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\"", conn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        var builder = new NpgsqlConnectionStringBuilder(BaseConnectionString);
        builder.Database = DatabaseName;
        ConnectionString = builder.ConnectionString;
    }

    public PostgreSQLStore CreateStore()
    {
        return new PostgreSQLStore(ConnectionString, NullLoggerFactory.Instance);
    }

    public JinagaClient CreateJinagaClient()
    {
        var store = CreateStore();
        return new JinagaClient(
            store,
            new Jinaga.DefaultImplementations.LocalNetwork(),
            System.Collections.Immutable.ImmutableList<Specification>.Empty,
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
                using (var cmd = new NpgsqlCommand(
                    $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
