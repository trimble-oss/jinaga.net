using Npgsql;

namespace Jinaga.Store.PostgreSQL.Database
{
    internal static class Migration
    {
        public static void CreateSchema(NpgsqlConnection conn)
        {
            using (var cmd = new NpgsqlCommand())
            {
                cmd.Connection = conn;
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS fact_type (
                        fact_type_id SERIAL PRIMARY KEY,
                        name TEXT NOT NULL
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_fact_type
                        ON fact_type (name);

                    CREATE TABLE IF NOT EXISTS fact (
                        fact_id SERIAL PRIMARY KEY,
                        fact_type_id INT NOT NULL REFERENCES fact_type (fact_type_id),
                        hash TEXT NOT NULL,
                        data TEXT NOT NULL,
                        date_learned TIMESTAMP NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC')
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_fact
                        ON fact (hash, fact_type_id);

                    CREATE TABLE IF NOT EXISTS role (
                        role_id SERIAL PRIMARY KEY,
                        defining_fact_type_id INT NOT NULL REFERENCES fact_type (fact_type_id),
                        name TEXT NOT NULL
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_role
                        ON role (defining_fact_type_id, name);

                    CREATE TABLE IF NOT EXISTS edge (
                        edge_id SERIAL PRIMARY KEY,
                        role_id INT NOT NULL REFERENCES role (role_id),
                        successor_fact_id INT NOT NULL REFERENCES fact (fact_id) ON DELETE CASCADE,
                        predecessor_fact_id INT NOT NULL REFERENCES fact (fact_id) ON DELETE CASCADE
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_edge
                        ON edge (successor_fact_id, predecessor_fact_id, role_id);

                    CREATE INDEX IF NOT EXISTS ix_successor
                        ON edge (successor_fact_id, role_id, predecessor_fact_id);

                    CREATE INDEX IF NOT EXISTS ix_predecessor
                        ON edge (predecessor_fact_id, role_id, successor_fact_id);

                    CREATE TABLE IF NOT EXISTS ancestor (
                        ancestor_id SERIAL PRIMARY KEY,
                        fact_id INT NOT NULL REFERENCES fact (fact_id) ON DELETE CASCADE,
                        ancestor_fact_id INT NOT NULL REFERENCES fact (fact_id) ON DELETE CASCADE
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_ancestor
                        ON ancestor (fact_id, ancestor_fact_id);

                    CREATE TABLE IF NOT EXISTS public_key (
                        public_key_id SERIAL PRIMARY KEY,
                        public_key TEXT NOT NULL
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_public_key
                        ON public_key (public_key);

                    CREATE TABLE IF NOT EXISTS signature (
                        signature_id SERIAL PRIMARY KEY,
                        fact_id INT NOT NULL REFERENCES fact (fact_id) ON DELETE CASCADE,
                        public_key_id INT NOT NULL REFERENCES public_key (public_key_id),
                        signature TEXT NOT NULL
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_signature
                        ON signature (fact_id, public_key_id);

                    CREATE TABLE IF NOT EXISTS ""user"" (
                        user_id SERIAL PRIMARY KEY,
                        provider TEXT NOT NULL,
                        user_identifier TEXT NOT NULL,
                        public_key_id INT NOT NULL REFERENCES public_key (public_key_id)
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_user
                        ON ""user"" (provider, user_identifier);

                    CREATE TABLE IF NOT EXISTS bookmark (
                        bookmark_id SERIAL PRIMARY KEY,
                        feed_hash TEXT NOT NULL,
                        bookmark TEXT NOT NULL
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_bookmark
                        ON bookmark (feed_hash);

                    CREATE TABLE IF NOT EXISTS mru (
                        mru_id SERIAL PRIMARY KEY,
                        specification_hash TEXT NOT NULL,
                        mru_date TIMESTAMP NOT NULL
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS ux_mru
                        ON mru (specification_hash);

                    CREATE TABLE IF NOT EXISTS outbound_queue (
                        queue_id SERIAL PRIMARY KEY,
                        fact_id INT NOT NULL,
                        graph_data TEXT NOT NULL
                    );
                ";
                cmd.ExecuteNonQuery();
            }
        }
    }
}
