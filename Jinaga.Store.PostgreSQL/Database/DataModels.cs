namespace Jinaga.Store.PostgreSQL.Database
{
    internal class FactWithIdAndSignatureFromDb
    {
        public int fact_id { get; set; }
        public string hash { get; set; }
        public string data { get; set; }
        public string name { get; set; }
        public string public_key { get; set; }
        public string signature { get; set; }
    }

    internal class ReferenceFromDb
    {
        public string hash { get; set; }
        public string name { get; set; }
    }

    internal class GraphFromDb
    {
        public int fact_id { get; set; }
        public string graph_data { get; set; }
    }

    internal class FactTypeFromDb
    {
        public int fact_type_id { get; set; }
        public string name { get; set; }
    }

    internal class RoleFromDb
    {
        public int role_id { get; set; }
        public int defining_fact_type_id { get; set; }
        public string name { get; set; }
    }
}
