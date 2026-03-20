using Jinaga.DefaultImplementations;
using Jinaga.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jinaga.Store.PostgreSQL.Test;

[Trait("Category", "PostgreSQL")]
public class NullTest : IClassFixture<PostgresTestFixture>
{
    [FactType("Test.Root")]
    record Root(string RootValue);

    [FactType("Test.Child")]
    record ChildFact(string Property1, string Property2);

    [FactType("Test.Parent")]
    record ParentFact(Root Root, ChildFact Child);

    private readonly PostgresTestFixture fixture;

    public NullTest(PostgresTestFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task ReproduceError()
    {
        var j = GivenJinagaClient();

        var root = new Root("Root");

        // Notice I'm passing in null for Property2, despite saying it is non-nullable.
        var child = new ChildFact("Property1", null!);
        var parent = new ParentFact(root, child);

        await j.Fact(parent);

        var results = await j.Local.Query(
                            Given<Root>.Match((root, facts) =>
                                facts.OfType<ParentFact>(parent => parent.Root == root)),
                            root);

        results.Should().ContainSingle().Which.Should().BeEquivalentTo(parent);

        await j.Unload();
    }

    private JinagaClient GivenJinagaClient(IStore? store = null)
    {
        var options = new JinagaClientOptions();
        return new JinagaClient(store ?? fixture.CreateStore(), new LocalNetwork(), [], NullLoggerFactory.Instance, options);
    }
}
