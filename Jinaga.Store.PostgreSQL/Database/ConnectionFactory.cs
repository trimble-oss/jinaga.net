using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Threading.Tasks;
using Npgsql;

namespace Jinaga.Store.PostgreSQL.Database
{
    internal class ConnectionFactory : IDisposable
    {
        private readonly string connectionString;
        private readonly NpgsqlDataSource dataSource;

        public ConnectionFactory(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));

            this.connectionString = connectionString;
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            this.dataSource = dataSourceBuilder.Build();
            RunMigrations();
        }

        private void RunMigrations()
        {
            using (var conn = dataSource.OpenConnection())
            {
                Migration.CreateSchema(conn);
            }
        }

        public T WithConn<T>(Func<NpgsqlConnection, T> callback)
        {
            using (var conn = dataSource.OpenConnection())
            {
                return callback(conn);
            }
        }

        public T WithTxn<T>(Func<NpgsqlConnection, T> callback)
        {
            using (var conn = dataSource.OpenConnection())
            using (var txn = conn.BeginTransaction())
            {
                try
                {
                    var result = callback(conn);
                    txn.Commit();
                    return result;
                }
                catch
                {
                    txn.Rollback();
                    throw;
                }
            }
        }

        public async Task<T> WithTxnAsync<T>(Func<NpgsqlConnection, T> callback)
        {
            return await Task.Run(() => WithTxn(callback)).ConfigureAwait(false);
        }

        public void Dispose()
        {
            dataSource?.Dispose();
        }
    }

    internal static class NpgsqlExtensions
    {
        public static int ExecuteNonQuery(this NpgsqlConnection conn, string sql, params object[] parameters)
        {
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                BindParameters(cmd, parameters);
                return cmd.ExecuteNonQuery();
            }
        }

        public static object ExecuteScalar(this NpgsqlConnection conn, string sql, params object[] parameters)
        {
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                BindParameters(cmd, parameters);
                return cmd.ExecuteScalar();
            }
        }

        public static string ExecuteScalarString(this NpgsqlConnection conn, string sql, params object[] parameters)
        {
            var result = ExecuteScalar(conn, sql, parameters);
            return result == null || result == DBNull.Value ? "" : result.ToString();
        }

        public static int? ExecuteScalarInt(this NpgsqlConnection conn, string sql, params object[] parameters)
        {
            var result = ExecuteScalar(conn, sql, parameters);
            if (result == null || result == DBNull.Value) return null;
            return Convert.ToInt32(result);
        }

        public static IEnumerable<T> ExecuteQuery<T>(this NpgsqlConnection conn, string sql, params object[] parameters) where T : new()
        {
            var results = new List<T>();
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                BindParameters(cmd, parameters);
                using (var reader = cmd.ExecuteReader())
                {
                    var properties = typeof(T).GetProperties();
                    while (reader.Read())
                    {
                        var item = new T();
                        foreach (var property in properties)
                        {
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                if (reader.GetName(i) == property.Name)
                                {
                                    var value = reader.GetValue(i);
                                    if (value != DBNull.Value)
                                    {
                                        if (property.PropertyType == typeof(int) && value is long longVal)
                                        {
                                            property.SetValue(item, (int)longVal);
                                        }
                                        else if (property.PropertyType == typeof(int) && value is string strVal)
                                        {
                                            property.SetValue(item, int.Parse(strVal));
                                        }
                                        else
                                        {
                                            property.SetValue(item, Convert.ChangeType(value, property.PropertyType));
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                        results.Add(item);
                    }
                }
            }
            return results;
        }

        public static IEnumerable<ImmutableDictionary<string, string>> ExecuteQueryRaw(this NpgsqlConnection conn, string sql, params object[] parameters)
        {
            var results = new List<ImmutableDictionary<string, string>>();
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                BindParameters(cmd, parameters);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var row = ImmutableDictionary<string, string>.Empty;
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            var value = reader.GetValue(i);
                            row = row.Add(reader.GetName(i), value == DBNull.Value ? null : value.ToString());
                        }
                        results.Add(row);
                    }
                }
            }
            return results;
        }

        private static void BindParameters(NpgsqlCommand cmd, object[] parameters)
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = "p" + i;
                param.Value = parameters[i] ?? DBNull.Value;
                cmd.Parameters.Add(param);
            }
        }
    }
}
