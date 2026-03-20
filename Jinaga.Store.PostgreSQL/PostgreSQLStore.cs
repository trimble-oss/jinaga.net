using Jinaga.Facts;
using Jinaga.Products;
using Jinaga.Projections;
using Jinaga.Services;
using Jinaga.Store.PostgreSQL.Builder;
using Jinaga.Store.PostgreSQL.Database;
using Jinaga.Store.PostgreSQL.Description;
using Jinaga.Store.PostgreSQL.Generation;
using Jinaga.Visualizers;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jinaga.Store.PostgreSQL
{
    public class PostgreSQLStore : IStore, IDisposable
    {
        private ConnectionFactory connFactory;
        private readonly ILogger logger;

        public PostgreSQLStore(string connectionString, ILoggerFactory loggerFactory)
        {
            this.connFactory = new ConnectionFactory(connectionString);
            this.logger = loggerFactory.CreateLogger<PostgreSQLStore>();
        }

        public bool IsPersistent => true;

        Task<ImmutableList<Fact>> IStore.Save(FactGraph graph, bool queue, CancellationToken cancellationToken)
        {
            if (graph.FactReferences.IsEmpty)
            {
                return Task.FromResult(ImmutableList<Fact>.Empty);
            }
            else
            {
                ImmutableList<Fact> newFacts = ImmutableList<Fact>.Empty;
                foreach (var factReference in graph.FactReferences)
                {
                    var envelope = graph.GetEnvelope(factReference);

                    connFactory.WithTxn(
                        (conn) =>
                        {
                            // Select or insert into FactType table. Gets a FactTypeId
                            var factTypeId = conn.ExecuteScalarString(
                                "SELECT fact_type_id FROM fact_type WHERE name = @p0",
                                envelope.Fact.Reference.Type);
                            if (factTypeId == "")
                            {
                                conn.ExecuteNonQuery(
                                    "INSERT INTO fact_type (name) VALUES (@p0) ON CONFLICT DO NOTHING",
                                    envelope.Fact.Reference.Type);
                                factTypeId = conn.ExecuteScalarString(
                                    "SELECT fact_type_id FROM fact_type WHERE name = @p0",
                                    envelope.Fact.Reference.Type);
                            }

                            // Select or insert into Fact table. Gets a FactId
                            var factId = conn.ExecuteScalarString(
                                "SELECT fact_id FROM fact WHERE hash = @p0 AND fact_type_id = @p1",
                                envelope.Fact.Reference.Hash, factTypeId);
                            if (factId == "")
                            {
                                newFacts = newFacts.Add(envelope.Fact);
                                string data = Fact.Canonicalize(envelope.Fact.Fields, envelope.Fact.Predecessors);
                                conn.ExecuteNonQuery(
                                    "INSERT INTO fact (fact_type_id, hash, data) VALUES (@p0, @p1, @p2) ON CONFLICT DO NOTHING",
                                    factTypeId, envelope.Fact.Reference.Hash, data);
                                factId = conn.ExecuteScalarString(
                                    "SELECT fact_id FROM fact WHERE hash = @p0 AND fact_type_id = @p1",
                                    envelope.Fact.Reference.Hash, factTypeId);

                                // Insert into the outbound_queue table
                                if (queue)
                                {
                                    var graphToQueue = graph.GetSubgraph(factReference);
                                    string graphData = graphToQueue.ToJson();
                                    conn.ExecuteNonQuery(
                                        "INSERT INTO outbound_queue (fact_id, graph_data) VALUES (@p0, @p1)",
                                        factId, graphData);
                                }

                                // For each predecessor of the inserted fact
                                foreach (var predecessor in envelope.Fact.Predecessors)
                                {
                                    // Select or insert into Role table. Gets a RoleId
                                    var roleId = conn.ExecuteScalarString(
                                        "SELECT role_id FROM role WHERE defining_fact_type_id = @p0 AND name = @p1",
                                        factTypeId, predecessor.Role);
                                    if (roleId == "")
                                    {
                                        conn.ExecuteNonQuery(
                                            "INSERT INTO role (defining_fact_type_id, name) VALUES (@p0, @p1) ON CONFLICT DO NOTHING",
                                            factTypeId, predecessor.Role);
                                        roleId = conn.ExecuteScalarString(
                                            "SELECT role_id FROM role WHERE defining_fact_type_id = @p0 AND name = @p1",
                                            factTypeId, predecessor.Role);
                                    }

                                    // Insert into Edge and Ancestor tables
                                    string predecessorFactId;
                                    switch (predecessor)
                                    {
                                        case PredecessorSingle s:
                                            predecessorFactId = GetFactId(conn, s.Reference);
                                            InsertEdge(conn, roleId, factId, predecessorFactId);
                                            InsertAncestors(conn, factId, predecessorFactId);
                                            break;
                                        case PredecessorMultiple m:
                                            foreach (var predecessorMultipleReference in m.References)
                                            {
                                                predecessorFactId = GetFactId(conn, predecessorMultipleReference);
                                                InsertEdge(conn, roleId, factId, predecessorFactId);
                                                InsertAncestors(conn, factId, predecessorFactId);
                                            }
                                            break;
                                        default:
                                            break;
                                    }
                                }
                            }

                            foreach (var signature in envelope.Signatures)
                            {
                                // Select or insert into the public_key table. Gets a public_key_id.
                                var publicKeyId = conn.ExecuteScalarString(
                                    "SELECT public_key_id FROM public_key WHERE public_key = @p0",
                                    signature.PublicKey);
                                if (publicKeyId == "")
                                {
                                    conn.ExecuteNonQuery(
                                        "INSERT INTO public_key (public_key) VALUES (@p0) ON CONFLICT DO NOTHING",
                                        signature.PublicKey);
                                    publicKeyId = conn.ExecuteScalarString(
                                        "SELECT public_key_id FROM public_key WHERE public_key = @p0",
                                        signature.PublicKey);
                                }

                                // Insert into the signature table if it doesn't already exist.
                                conn.ExecuteNonQuery(
                                    "INSERT INTO signature (fact_id, public_key_id, signature) VALUES (@p0, @p1, @p2) ON CONFLICT DO NOTHING",
                                    factId, publicKeyId, signature.Signature);
                            }
                            return 0;
                        }
                    );
                }
                logger.LogInformation("PostgreSQL saved {count} facts", newFacts.Count);
                return Task.FromResult(newFacts);
            }
        }

        private string GetFactId(NpgsqlConnection conn, FactReference factReference)
        {
            var factTypeId = conn.ExecuteScalarString(
                "SELECT fact_type_id FROM fact_type WHERE name = @p0",
                factReference.Type);

            return conn.ExecuteScalarString(
                "SELECT fact_id FROM fact WHERE hash = @p0 AND fact_type_id = @p1",
                factReference.Hash, factTypeId);
        }

        private void InsertAncestors(NpgsqlConnection conn, string factId, string predecessorFactId)
        {
            conn.ExecuteNonQuery(@"
                INSERT INTO ancestor (fact_id, ancestor_fact_id)
                SELECT @p0::int, @p1::int
                UNION
                SELECT @p0::int, ancestor_fact_id
                FROM ancestor
                WHERE fact_id = @p1::int
                ON CONFLICT DO NOTHING
            ", factId, predecessorFactId);
        }

        private void InsertEdge(NpgsqlConnection conn, string roleId, string successorFactId, string predecessorFactId)
        {
            conn.ExecuteNonQuery(
                "INSERT INTO edge (role_id, successor_fact_id, predecessor_fact_id) VALUES (@p0, @p1, @p2) ON CONFLICT DO NOTHING",
                roleId, successorFactId, predecessorFactId);
        }

        Task<FactGraph> IStore.Load(ImmutableList<FactReference> references, CancellationToken cancellationToken)
        {
            if (references.IsEmpty)
            {
                return Task.FromResult(FactGraph.Empty);
            }
            else
            {
                var factsFromDb = connFactory.WithConn(
                    (conn) =>
                    {
                        var parameters = new List<object>();
                        var referenceValues = new List<string>();
                        for (int i = 0; i < references.Count; i++)
                        {
                            referenceValues.Add($"(@p{2 * i}, @p{2 * i + 1})");
                            parameters.Add(references[i].Hash);
                            parameters.Add(references[i].Type);
                        }

                        string sql = $@"
                            SELECT
                                f.fact_id,
                                f.hash, 
                                f.data,
                                t.name,
                                p.public_key,
                                s.signature
                            FROM fact f 
                            JOIN fact_type t 
                                ON f.fact_type_id = t.fact_type_id
                            LEFT JOIN signature s
                                ON s.fact_id = f.fact_id
                            LEFT JOIN public_key p
                                ON p.public_key_id = s.public_key_id
                            WHERE (f.hash, t.name) 
                                IN (VALUES {String.Join(",", referenceValues)} )

                        UNION 

                            SELECT
                                f2.fact_id,
                                f2.hash, 
                                f2.data,
                                t2.name,
                                p.public_key,
                                s.signature
                            FROM fact f1 
                            JOIN fact_type t1 
                                ON 
                                    f1.fact_type_id = t1.fact_type_id
                                        AND
                                    (f1.hash, t1.name) IN (VALUES {String.Join(",", referenceValues)} ) 
                            JOIN ancestor a 
                                ON a.fact_id = f1.fact_id 
                            JOIN fact f2 
                                ON f2.fact_id = a.ancestor_fact_id 
                            JOIN fact_type t2 
                                ON t2.fact_type_id = f2.fact_type_id
                            LEFT JOIN signature s
                                ON s.fact_id = f2.fact_id
                            LEFT JOIN public_key p
                                ON p.public_key_id = s.public_key_id
                        ";

                        return conn.ExecuteQuery<FactWithIdAndSignatureFromDb>(sql, parameters.ToArray());
                    }
                );

                logger.LogTrace("PostgreSQL loaded {count} facts", factsFromDb.Count());

                FactGraphBuilder fb = new FactGraphBuilder();

                foreach (FactEnvelope envelope in factsFromDb.Deserialize())
                {
                    fb.Add(envelope);
                }

                return Task.FromResult(fb.Build());
            }
        }

        public Task<ImmutableList<FactReference>> ListKnown(ImmutableList<FactReference> factReferences)
        {
            if (factReferences.IsEmpty)
            {
                return Task.FromResult(ImmutableList<FactReference>.Empty);
            }
            else
            {
                var referencesFromDb = connFactory.WithConn(
                    (conn) =>
                    {
                        var parameters = new List<object>();
                        var referenceValues = new List<string>();
                        for (int i = 0; i < factReferences.Count; i++)
                        {
                            referenceValues.Add($"(@p{2 * i}, @p{2 * i + 1})");
                            parameters.Add(factReferences[i].Hash);
                            parameters.Add(factReferences[i].Type);
                        }

                        string sql = $@"
                            SELECT f.hash, 
                                   t.name
                            FROM fact f 
                            JOIN fact_type t 
                                ON f.fact_type_id = t.fact_type_id    
                            WHERE (f.hash, t.name) 
                                IN (VALUES {String.Join(",", referenceValues)} )
                        ";

                        return conn.ExecuteQuery<ReferenceFromDb>(sql, parameters.ToArray());
                    }
                );

                var knownReferences = referencesFromDb
                    .Select(r => new FactReference(r.name, r.hash))
                    .ToImmutableList();

                logger.LogTrace("PostgreSQL listed {knownCount} known facts of {givenCount}", knownReferences.Count, factReferences.Count);
                return Task.FromResult(knownReferences);
            }
        }

        Task<ImmutableList<Product>> IStore.Read(FactReferenceTuple givenTuple, Specification specification, CancellationToken cancellationToken)
        {
            var factTypes = LoadFactTypesFromSpecification(specification);
            var factTypeMap = factTypes.Select(factType => KeyValuePair.Create(factType.name, factType.fact_type_id)).ToImmutableDictionary();
            
            var roles = LoadRolesFromSpecification(specification, factTypes);
            var roleMap = roles
                .GroupBy(
                    role => role.defining_fact_type_id, 
                    role => KeyValuePair.Create(role.name, role.role_id)
                )
                .Select(
                    pair => KeyValuePair.Create(pair.Key, pair.ToImmutableDictionary())
                )
                .ToImmutableDictionary();                                    

            var descriptionBuilder = new ResultDescriptionBuilder(factTypeMap, roleMap);
            
            var description = descriptionBuilder.Build(givenTuple, specification);

            if (!description.QueryDescription.IsSatisfiable())
            {
                return Task.FromResult(ImmutableList<Product>.Empty);
            }
            var sqlQueryTree = SqlGenerator.CreateSqlQueryTree(description);

            ResultSetTree resultSets = connFactory.WithConn(
                (conn) =>
                {
                    return ExecuteQueryTree(sqlQueryTree, conn);
                }
            );

            var givenProduct = Product.Empty;
            foreach (var given in specification.Givens)
            {
                var reference = givenTuple.Get(given.Label.Name);
                givenProduct = givenProduct.With(
                    given.Label.Name,
                    new SimpleElement(reference)
                );
            }

            logger.LogInformation("PostgreSQL read {count} facts", resultSets.Count);
            return Task.FromResult(sqlQueryTree.ResultsToProducts(resultSets, givenProduct));
        }

        private ResultSetTree ExecuteQueryTree(SqlQueryTree sqlQueryTree, NpgsqlConnection conn)
        {
            var resultSetTree = ExecuteQuery(sqlQueryTree, conn);

            foreach (var childQuery in sqlQueryTree.ChildQueries)
            {
                var childResultSet = ExecuteQueryTree(childQuery.Value, conn);
                resultSetTree.ChildResultSets = resultSetTree.ChildResultSets.Add(childQuery.Key, childResultSet);
            };

            return resultSetTree;
        }

        private static ResultSetTree ExecuteQuery(SqlQueryTree sqlQueryTree, NpgsqlConnection conn)
        {
            var sqlQuery = sqlQueryTree.SqlQuery;
            if (string.IsNullOrEmpty(sqlQuery.Sql))
            {
                return new ResultSetTree();
            }

            // Convert $N parameters to @pN parameters for Npgsql
            var sql = ConvertParameterSyntax(sqlQueryTree.SqlQuery.Sql);
            var dataRows = conn.ExecuteQueryRaw(sql, sqlQueryTree.SqlQuery.Parameters.ToArray());
            var resultSet = dataRows.Select(dataRow =>
            {
                var resultSetRow = sqlQuery.Labels.Aggregate(ImmutableDictionary<int, ResultSetFact>.Empty,
                                                    (acc, next) =>
                                                    {
                                                        var fact = new ResultSetFact();
                                                        fact.Hash = dataRow[$"hash{next.Index}"];
                                                        fact.FactId = int.Parse(dataRow[$"id{next.Index}"]);
                                                        fact.Data = dataRow[$"data{next.Index}"];
                                                        fact.Type = next.Type;
                                                        fact.Name = next.Name;
                                                        return acc.Add(next.Index, fact);
                                                    });

                return resultSetRow;
            });

            var resultSetTree = new ResultSetTree();
            resultSetTree.ResultSet = resultSet.ToImmutableList();
            return resultSetTree;
        }

        /// <summary>
        /// Converts $N parameter syntax (used by SqlGenerator) to @pN-1 syntax for Npgsql.
        /// $1 becomes @p0, $2 becomes @p1, etc.
        /// </summary>
        internal static string ConvertParameterSyntax(string sql)
        {
            // Replace $N with @p(N-1) for Npgsql
            // Process in reverse order of parameter number to avoid replacing $1 in $10
            var result = sql;
            for (int i = 100; i >= 1; i--)
            {
                result = result.Replace($"${i}", $"@p{i - 1}");
            }
            return result;
        }

        private IEnumerable<FactTypeFromDb> LoadFactTypesFromSpecification(Specification specification)
        {
            var factTypeResult = connFactory.WithConn(
                (conn) =>
                {
                    return conn.ExecuteQuery<FactTypeFromDb>(
                        "SELECT fact_type_id, name FROM fact_type");
                }
            );
            return factTypeResult;
        }

        private IEnumerable<RoleFromDb> LoadRolesFromSpecification(Specification specification, object factTypes)
        {
            var rolesResult = connFactory.WithConn(
                (conn) =>
                {
                    return conn.ExecuteQuery<RoleFromDb>(
                        "SELECT role_id, defining_fact_type_id, name FROM role");
                }
            );
            return rolesResult;
        }

        private IEnumerable<FactTypeFromDb> LoadFactTypesFromReferences(ImmutableList<FactReference> references)
        {
            var factTypeResult = connFactory.WithConn(
                (conn) =>
                {
                    return conn.ExecuteQuery<FactTypeFromDb>(
                        "SELECT fact_type_id, name FROM fact_type");
                }
            );
            return factTypeResult;
        }

        public Task SaveBookmark(string feed, string bookmark)
        {
            connFactory.WithTxn(
                (conn) =>
                {
                    return conn.ExecuteNonQuery(@"
                        INSERT INTO bookmark (feed_hash, bookmark)
                        VALUES (@p0, @p1)
                        ON CONFLICT (feed_hash)
                        DO UPDATE SET bookmark = @p1
                    ", feed, bookmark);
                }
            );
            return Task.FromResult("");
        }

        public Task<string> LoadBookmark(string feed)
        {
            var bookMark = connFactory.WithTxn(
                (conn) =>
                {
                    return conn.ExecuteScalarString(
                        "SELECT bookmark FROM bookmark WHERE feed_hash = @p0",
                        feed);
                }
            );
            return Task.FromResult(bookMark);
        }

        public Task SetMruDate(string specificationHash, DateTime mruDate)
        {
            connFactory.WithTxn(
                (conn) =>
                {
                    return conn.ExecuteNonQuery(@"
                        INSERT INTO mru (specification_hash, mru_date)
                        VALUES (@p0, @p1)
                        ON CONFLICT (specification_hash)
                        DO UPDATE SET mru_date = @p1
                    ", specificationHash, mruDate.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                }
            );
            return Task.FromResult("");
        }

        public Task<DateTime?> GetMruDate(string specificationHash)
        {
            string mruDateString = connFactory.WithTxn(
                (conn) =>
                {
                    return conn.ExecuteScalarString(
                        "SELECT mru_date FROM mru WHERE specification_hash = @p0",
                        specificationHash);
                }
            );
            DateTime mruDate;
            if (DateTime.TryParseExact(mruDateString, "yyyy-MM-dd HH:mm:ss", null, DateTimeStyles.AssumeUniversal, out mruDate))
            {
                return Task.FromResult((DateTime?)mruDate.ToUniversalTime());
            }
            else
            {
                return Task.FromResult((DateTime?)null);
            }
        }

        public Task<QueuedFacts> GetQueue()
        {
            var graphsFromDb = connFactory.WithConn(
                (conn) =>
                {
                    return conn.ExecuteQuery<GraphFromDb>(@"
                        SELECT q.fact_id, q.graph_data
                        FROM outbound_queue q
                        ORDER BY q.queue_id
                    ");
                }
            );

            var factGraphs = graphsFromDb.Select(g => DeserializeFactGraph(g.graph_data)).ToImmutableList();

            int lastFactId = 0;
            if (factGraphs.Count > 0)
            {
                lastFactId = graphsFromDb.Max(g => g.fact_id);
            }

            var mergedGraph = FactGraph.Empty;
            foreach (var graph in factGraphs)
            {
                mergedGraph = mergedGraph.Merge(graph);
            }

            logger.LogTrace("PostgreSQL read {count} queued graphs", factGraphs.Count);
            return Task.FromResult(new QueuedFacts(mergedGraph, lastFactId.ToString()));
        }

        private FactGraph DeserializeFactGraph(string json)
        {
            var envelopes = new List<FactEnvelope>();
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var type = element.GetProperty("type").GetString();
                    var factElement = element.GetProperty("fact");
                    var fields = DeserializeFields(factElement.GetProperty("fields"));
                    var predecessors = DeserializePredecessors(factElement.GetProperty("predecessors"));
                    var fact = Fact.Create(type, fields, predecessors);

                    var signatures = element.GetProperty("signatures")
                        .EnumerateArray()
                        .Select(s => new FactSignature(s.GetProperty("publicKey").GetString(), s.GetProperty("signature").GetString()))
                        .ToImmutableList();

                    envelopes.Add(new FactEnvelope(fact, signatures));
                }
            }

            var builder = new FactGraphBuilder();
            foreach (var envelope in envelopes)
            {
                builder.Add(envelope);
            }
            return builder.Build();
        }

        private ImmutableList<Field> DeserializeFields(JsonElement fieldsElement)
        {
            var fields = ImmutableList<Field>.Empty;
            foreach (var field in fieldsElement.EnumerateObject())
            {
                switch (field.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        fields = fields.Add(new Field(field.Name, new FieldValueString(field.Value.GetString())));
                        break;
                    case JsonValueKind.Number:
                        fields = fields.Add(new Field(field.Name, new FieldValueNumber(field.Value.GetDouble())));
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        fields = fields.Add(new Field(field.Name, new FieldValueBoolean(field.Value.GetBoolean())));
                        break;
                    case JsonValueKind.Null:
                        fields = fields.Add(new Field(field.Name, FieldValue.Null));
                        break;
                }
            }
            return fields;
        }

        private ImmutableList<Predecessor> DeserializePredecessors(JsonElement predecessorsElement)
        {
            var predecessors = ImmutableList<Predecessor>.Empty;
            foreach (var predecessor in predecessorsElement.EnumerateObject())
            {
                switch (predecessor.Value.ValueKind)
                {
                    case JsonValueKind.Object:
                        var hash = predecessor.Value.GetProperty("hash").GetString();
                        var type = predecessor.Value.GetProperty("type").GetString();
                        predecessors = predecessors.Add(new PredecessorSingle(predecessor.Name, new FactReference(type, hash)));
                        break;
                    case JsonValueKind.Array:
                        var factReferences = ImmutableList<FactReference>.Empty;
                        foreach (var factReference in predecessor.Value.EnumerateArray())
                        {
                            hash = factReference.GetProperty("hash").GetString();
                            type = factReference.GetProperty("type").GetString();
                            factReferences = factReferences.Add(new FactReference(type, hash));
                        }
                        predecessors = predecessors.Add(new PredecessorMultiple(predecessor.Name, factReferences));
                        break;
                }
            }
            return predecessors;
        }

        public Task SetQueueBookmark(string bookmark)
        {
            connFactory.WithTxn(
                (conn) =>
                {
                    if (int.TryParse(bookmark, out int lastFactId))
                    {
                        conn.ExecuteNonQuery(
                            "DELETE FROM outbound_queue WHERE fact_id <= @p0",
                            lastFactId);
                    }
                    return 0;
                }
            );

            return Task.CompletedTask;
        }

        public Task<IEnumerable<Fact>> GetAllFacts()
        {
            var factsFromDb = connFactory.WithConn(
                (conn) =>
                {
                    return conn.ExecuteQuery<FactWithIdAndSignatureFromDb>(@"
                        SELECT f.fact_id, f.hash, f.data, t.name, p.public_key, s.signature
                        FROM fact f
                        JOIN fact_type t
                            ON f.fact_type_id = t.fact_type_id
                        LEFT JOIN signature s
                            ON s.fact_id = f.fact_id
                        LEFT JOIN public_key p
                            ON p.public_key_id = s.public_key_id
                    ");
                }
            );

            var envelopes = factsFromDb.Deserialize();
            var facts = envelopes.Select(envelope => envelope.Fact);
            return Task.FromResult(facts);
        }

        public Task Purge(ImmutableList<Specification> purgeConditions)
        {
            foreach (var specification in purgeConditions)
            {
                var label = specification.Givens.Single().Label;
                var givenTuple = FactReferenceTuple.Empty
                    .Add(label.Name, new FactReference(label.Type, "xxxx"));
                var factTypes = LoadFactTypesFromSpecification(specification);
                var factTypeMap = factTypes.Select(factType => KeyValuePair.Create(factType.name, factType.fact_type_id)).ToImmutableDictionary();
                
                var roles = LoadRolesFromSpecification(specification, factTypes);
                var roleMap = roles
                    .GroupBy(
                        role => role.defining_fact_type_id, 
                        role => KeyValuePair.Create(role.name, role.role_id)
                    )
                    .Select(
                        pair => KeyValuePair.Create(pair.Key, pair.ToImmutableDictionary())
                    )
                    .ToImmutableDictionary();                                    

                var descriptionBuilder = new ResultDescriptionBuilder(factTypeMap, roleMap);
                
                var description = descriptionBuilder.Build(givenTuple, specification);

                if (!description.QueryDescription.IsSatisfiable())
                {
                    continue;
                }

                (string sql, ImmutableList<object> parameters) = PurgeSqlFromSpecification(description);
                connFactory.WithConn((conn) =>
                {
                    return conn.ExecuteNonQuery(sql, parameters.ToArray());
                });
            }
            return Task.CompletedTask;
        }

        private (string sql, ImmutableList<object> parameters) PurgeSqlFromSpecification(ResultDescription description)
        {
            var queryDescription = description.QueryDescription;
            if (queryDescription.ExistentialConditions.Count > 0)
            {
                throw new ArgumentException("Purge conditions should not have existential conditions");
            }

            var columns = queryDescription.Outputs
                .Select((label, index) => $"f{label.FactIndex}.fact_id as trigger{index + 1}")
                .Join(", ");
            var firstEdge = queryDescription.Edges.First();
            var predecessorInput = queryDescription.Inputs.Find(input => input.FactIndex == firstEdge.PredecessorFactIndex);
            var successorInput = queryDescription.Inputs.Find(input => input.FactIndex == firstEdge.SuccessorFactIndex);
            var firstFactIndex = predecessorInput != null ? predecessorInput.FactIndex : successorInput.FactIndex;
            var writtenFactIndexes = new HashSet<int> { firstFactIndex };
            var joins = GenerateJoins(queryDescription.Edges, writtenFactIndexes);
            var inputWhereClauses = queryDescription.Inputs
                .Select(input => $"f{input.FactIndex}.fact_type_id = @p{input.FactTypeParameter - 2}")
                .Join(" AND ");

            var triggerWhereClauses = queryDescription.Outputs
                .Select((label, index) => $"a.fact_id = c2.trigger{index + 1}")
                .Join("\n            OR ");
            var triggerAncestorClauses = queryDescription.Outputs
                .Select((label, index) =>
                    $"    AND NOT EXISTS (\n" +
                    $"        SELECT 1\n" +
                    $"        FROM candidates c2\n" +
                    $"        JOIN ancestor a2\n" +
                    $"            ON a2.fact_id = c2.trigger{index + 1}\n" +
                    $"        WHERE a.fact_id = a2.ancestor_fact_id\n" +
                    $"    )\n"
                )
                .Join("");

            var sql = $@"
WITH candidates AS (
    SELECT
        f{firstFactIndex}.fact_id as purge_root,
        {columns}
    FROM fact f{firstFactIndex}
    {joins.Join("")}
    WHERE {inputWhereClauses}
), targets AS (
    SELECT a.fact_id
    FROM ancestor a
    JOIN candidates c ON c.purge_root = a.ancestor_fact_id
    WHERE NOT EXISTS (
        SELECT 1
        FROM candidates c2
        WHERE {triggerWhereClauses}
    )
    {triggerAncestorClauses}
)
DELETE
FROM fact
WHERE fact_id IN (SELECT fact_id FROM targets);";
            var parameters = queryDescription.Parameters.RemoveAt(1);

            return (sql, parameters);
        }

        private static ImmutableList<string> GenerateJoins(ImmutableList<EdgeDescription> edges, HashSet<int> writtenFactIndexes)
        {
            var joins = ImmutableList<string>.Empty;
            var remainingEdges = edges;
            while (remainingEdges.Count > 0)
            {
                var edgeIndex = remainingEdges.FindIndex(edge =>
                    writtenFactIndexes.Contains(edge.PredecessorFactIndex) ||
                    writtenFactIndexes.Contains(edge.SuccessorFactIndex)
                );

                if (edgeIndex < 0)
                {
                    throw new ArgumentException("The specification is not connected");
                }

                var edge = remainingEdges[edgeIndex];
                remainingEdges = remainingEdges.RemoveAt(edgeIndex);

                if (writtenFactIndexes.Contains(edge.PredecessorFactIndex))
                {
                    if (writtenFactIndexes.Contains(edge.SuccessorFactIndex))
                    {
                        joins = joins.Add(
                            $" JOIN edge e{edge.EdgeIndex}" +
                            $" ON e{edge.EdgeIndex}.predecessor_fact_id = f{edge.PredecessorFactIndex}.fact_id" +
                            $" AND e{edge.EdgeIndex}.successor_fact_id = f{edge.SuccessorFactIndex}.fact_id" +
                            $" AND e{edge.EdgeIndex}.role_id = @p{edge.RoleParameter - 2}"
                        );
                    }
                    else
                    {
                        joins = joins.Add(
                            $" JOIN edge e{edge.EdgeIndex}" +
                            $" ON e{edge.EdgeIndex}.predecessor_fact_id = f{edge.PredecessorFactIndex}.fact_id" +
                            $" AND e{edge.EdgeIndex}.role_id = @p{edge.RoleParameter - 2}"
                        );
                        joins = joins.Add(
                            $" JOIN fact f{edge.SuccessorFactIndex}" +
                            $" ON f{edge.SuccessorFactIndex}.fact_id = e{edge.EdgeIndex}.successor_fact_id"
                        );
                        writtenFactIndexes.Add(edge.SuccessorFactIndex);
                    }
                }
                else if (writtenFactIndexes.Contains(edge.SuccessorFactIndex))
                {
                    joins = joins.Add(
                        $" JOIN edge e{edge.EdgeIndex}" +
                        $" ON e{edge.EdgeIndex}.successor_fact_id = f{edge.SuccessorFactIndex}.fact_id" +
                        $" AND e{edge.EdgeIndex}.role_id = @p{edge.RoleParameter - 2}"
                    );
                    joins = joins.Add(
                        $" JOIN fact f{edge.PredecessorFactIndex}" +
                        $" ON f{edge.PredecessorFactIndex}.fact_id = e{edge.EdgeIndex}.predecessor_fact_id"
                    );
                    writtenFactIndexes.Add(edge.PredecessorFactIndex);
                }
                else
                {
                    throw new ArgumentException("Neither predecessor nor successor fact has been written");
                }
            }
            return joins;
        }

        public Task PurgeDescendants(FactReference purgeRoot, ImmutableList<FactReference> triggers)
        {
            var factTypes = LoadFactTypesFromReferences(new[] { purgeRoot }.Concat(triggers).ToImmutableList())
                .ToImmutableDictionary(ft => ft.name, ft => ft.fact_type_id);
            if (!factTypes.ContainsKey(purgeRoot.Type) || triggers.Any(t => !factTypes.ContainsKey(t.Type)))
            {
                return Task.CompletedTask;
            }

            var parameters = new List<object>
            {
                factTypes[purgeRoot.Type],
                purgeRoot.Hash
            };
            parameters.AddRange(triggers.SelectMany(t => new object[] { factTypes[t.Type], t.Hash }));

            var purgeCommand = PurgeDescendantsSql(triggers.Count);

            connFactory.WithTxn(
                (conn) =>
                {
                    conn.ExecuteNonQuery(purgeCommand, parameters.ToArray());
                    return 0;
                }
            );

            return Task.CompletedTask;
        }

        private string PurgeDescendantsSql(int triggerCount)
        {
            var whereClause = "    WHERE (t.fact_type_id = @p2 AND t.hash = @p3)\n";
            for (int i = 1; i < triggerCount; i++)
            {
                whereClause += $"        OR (t.fact_type_id = @p{i * 2 + 2} AND t.hash = @p{i * 2 + 3})\n";
            }

            var sql =
                "WITH purge_root AS (\n" +
                "    SELECT pr.fact_id\n" +
                "    FROM fact pr\n" +
                "    WHERE pr.fact_type_id = @p0\n" +
                "        AND pr.hash = @p1\n" +
                "), triggers AS (\n" +
                "    SELECT t.fact_id\n" +
                "    FROM fact t\n" +
                whereClause +
                "), triggers_and_ancestors AS (\n" +
                "    SELECT t.fact_id\n" +
                "    FROM triggers t\n" +
                "    UNION\n" +
                "    SELECT a.ancestor_fact_id\n" +
                "    FROM ancestor a\n" +
                "    JOIN triggers t\n" +
                "        ON a.fact_id = t.fact_id\n" +
                "), targets AS (\n" +
                "    SELECT a.fact_id\n" +
                "    FROM ancestor a\n" +
                "    JOIN purge_root pr\n" +
                "        ON a.ancestor_fact_id = pr.fact_id\n" +
                "    WHERE a.fact_id NOT IN (SELECT * FROM triggers_and_ancestors)\n" +
                ")\n" +
                "DELETE\n" +
                "FROM fact\n" +
                "WHERE fact_id IN (SELECT fact_id FROM targets)\n";
            return sql;
        }

        public void Dispose()
        {
            connFactory?.Dispose();
        }
    }
}
