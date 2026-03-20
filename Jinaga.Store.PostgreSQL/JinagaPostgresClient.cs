using Jinaga.DefaultImplementations;
using Jinaga.Services;
using Jinaga.Http;
using Jinaga.Projections;
using Jinaga.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Immutable;

namespace Jinaga.Store.PostgreSQL
{
    public class JinagaPostgresClientOptions : JinagaClientOptions
    {
        public string ConnectionString { get; set; }
    }

    public static class JinagaPostgresClient
    {
        public static JinagaClient Create()
        {
            return Create(_ => { });
        }

        public static JinagaClient Create(Action<JinagaPostgresClientOptions> configure)
        {
            var options = new JinagaPostgresClientOptions();
            configure(options);

            var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;

            IStore store = options.ConnectionString == null
                ? (IStore)new MemoryStore()
                : new PostgreSQLStore(options.ConnectionString, loggerFactory);

            INetwork network = options.HttpEndpoint == null
                ? (INetwork)new LocalNetwork()
                : new HttpNetwork(options.HttpEndpoint, options.HttpAuthenticationProvider, loggerFactory, options.RetryConfiguration);

            var purgeConditions = CreatePurgeConditions(options);
            return new JinagaClient(store, network, purgeConditions, loggerFactory, options);
        }

        private static ImmutableList<Specification> CreatePurgeConditions(JinagaClientOptions options)
        {
            if (options.PurgeConditions == null)
            {
                return ImmutableList<Specification>.Empty;
            }
            else
            {
                var purgeConditions = options.PurgeConditions(PurgeConditions.Empty);
                return purgeConditions.Validate();
            }
        }
    }
}
