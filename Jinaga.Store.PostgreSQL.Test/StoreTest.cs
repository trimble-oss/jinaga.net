using Jinaga.DefaultImplementations;
using Jinaga.Managers;
using Jinaga.Facts;
using Jinaga.Services;
using Jinaga.Store.PostgreSQL.Test.Models;
using System.Collections.Immutable;
using Xunit.Abstractions;
using Jinaga.Storage;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jinaga.Store.PostgreSQL.Test;

[Trait("Category", "PostgreSQL")]
public class StoreTest : IClassFixture<PostgresTestFixture>
{
    private readonly ITestOutputHelper output;
    private readonly PostgresTestFixture fixture;

    public StoreTest(PostgresTestFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact]
    public async Task SaveAndLoadBookmark()
    {
        IStore store = fixture.CreateStore();
        await store.SaveBookmark("myFeedHashA", "bookmarkA_1");
        var bmA1 = await store.LoadBookmark("myFeedHashA");

        await store.SaveBookmark("myFeedHashB", "bookmarkB_1");
        var bmB1 = await store.LoadBookmark("myFeedHashB");

        await store.SaveBookmark("myFeedHashA", "bookmarkA_2");
        var bmA2 = await store.LoadBookmark("myFeedHashA");

        var bmU1 = await store.LoadBookmark("unknownFeed");

        bmA1.Should().Be("bookmarkA_1");
        bmB1.Should().Be("bookmarkB_1");
        bmA2.Should().Be("bookmarkA_2");
        bmU1.Should().Be("");
    }

    [Fact]
    public async Task SaveAndLoadMru()
    {
        IStore store = fixture.CreateStore();

        DateTime nowA1 = DateTime.Parse("2023-08-23T18:39:43");
        await store.SetMruDate("mySpecificationHashA", nowA1);
        var mruA1 = await store.GetMruDate("mySpecificationHashA");
        mruA1.Should().Be(nowA1.ToUniversalTime());

        DateTime nowB1 = DateTime.Parse("2023-08-20T10:39:43");
        await store.SetMruDate("mySpecificationHashB", nowB1);
        var mruB1 = await store.GetMruDate("mySpecificationHashB");
        mruB1.Should().Be(nowB1.ToUniversalTime());

        DateTime nowA2 = DateTime.Parse("2023-08-21T07:39:01");
        await store.SetMruDate("mySpecificationHashA", nowA2);
        var mruA2 = await store.GetMruDate("mySpecificationHashA");
        mruA2.Should().Be(nowA2.ToUniversalTime());

        var mruU = await store.GetMruDate("UnknownSpecificationHash");
        mruU.Should().BeNull();
    }

    [Fact]
    public async Task MruRespectUTC()
    {
        IStore store = fixture.CreateStore();

        DateTime nowC1 = DateTime.Parse("2023-08-23T18:39:43Z", null, DateTimeStyles.AdjustToUniversal);
        await store.SetMruDate("mySpecificationHashC", nowC1);
        var mruC1 = await store.GetMruDate("mySpecificationHashC");
        nowC1.Kind.Should().Be(DateTimeKind.Utc);
        mruC1.Should().Be(nowC1);
        mruC1?.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task CanQueryForSuccessors()
    {
        var j = GivenJinagaClient();

        var airlineDay = await j.Fact(new AirlineDay(new Airline("IA"), DateTime.Parse("2021-07-04T01:39:43.241Z").Date));
        var flight = await j.Fact(new Flight(airlineDay, 4247));

        var specification = Given<AirlineDay>.Match((airlineDay, facts) =>
            from flight in facts.OfType<Flight>()
            where flight.airlineDay == airlineDay
            select flight.flightNumber
        );

        var flightNumbers = await j.Query(specification, airlineDay);
        flightNumbers.Should().ContainSingle().Which.Should().Be(4247);
    }

    [Fact]
    public async Task StoreRoundTripToUTC()
    {
        DateTime now = DateTime.Parse("2021-07-04T01:39:43.241Z");
        var j = GivenJinagaClient();
        var airlineDay = await j.Fact(new AirlineDay(new Airline("Airline1"), now));
        var flight = await j.Fact(new Flight(airlineDay, 555));
        airlineDay.date.Kind.Should().Be(DateTimeKind.Utc);
        airlineDay.date.Hour.Should().Be(1);
    }

    [Fact]
    public async Task SavePredecessorMultiple()
    {
        DateTime now = DateTime.Parse("2021-07-04T01:39:43.241Z");
        var j = GivenJinagaClient();
        var airline = new Airline("Airline1");
        var user = await j.Fact(new User("fqjsdfqkfjqlm"));
        var passenger = await j.Fact(new Passenger(airline, user));
        var passengerName1 = await j.Fact(new PassengerName(passenger, "Michael", new PassengerName[0]));
        var passengerName2 = await j.Fact(new PassengerName(passenger, "Caden", new PassengerName[0]));
        var passengerName3 = await j.Fact(new PassengerName(passenger, "Jan", new PassengerName[] { passengerName1, passengerName2 }));

        passengerName3.value.Should().Be("Jan");
    }

    [Fact]
    public async Task LoadNothingFromStore()
    {
        IStore store = fixture.CreateStore();
        FactGraph factGraph = await store.Load(ImmutableList<FactReference>.Empty, default);
        factGraph.FactReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadFromStore()
    {
        IStore store = fixture.CreateStore();

        DateTime now = DateTime.Parse("2021-07-04T01:39:43.241Z");
        var j = GivenJinagaClient(store);
        var airline = new Airline("Airline1");
        var user = await j.Fact(new User("fqjsdf'qkfjqlm"));
        var passenger = await j.Fact(new Passenger(airline, user));
        var passengerName1 = await j.Fact(new PassengerName(passenger, "Michael", new PassengerName[0]));
        var passengerName2 = await j.Fact(new PassengerName(passenger, "Caden", new PassengerName[0]));
        var passengerName3 = await j.Fact(new PassengerName(passenger, "Jan", new PassengerName[] { passengerName1, passengerName2 }));

        var lastRef = ReferenceOfFact(passengerName3);

        var factGraph = await store.Load(ImmutableList<FactReference>.Empty.Add(lastRef), default);
        factGraph.FactReferences.Count.Should().Be(6);
    }

    [Fact]
    public async Task LoadFromStoreDWS()
    {
        IStore store = fixture.CreateStore();

        DateTime now = DateTime.Parse("2021-07-04T01:39:43.241Z");
        var j = GivenJinagaClient(store);

        var supplier = await j.Fact(new Supplier("abc-pubkey"));
        var client = await j.Fact(new Client(supplier, now));
        var yard = await j.Fact(new Yard(client, now));
        var yardAddress1 = await j.Fact(new YardAddress(yard, "myYardName1", "myRemark", "myStreet", "myHousNb", "myPostalCode", "myCity", "myCountry", new YardAddress[0]));
        var yardAddress2 = await j.Fact(new YardAddress(yard, "myYardName2", "myRemark", "myStreet", "myHousNb", "myPostalCode", "myCity", "myCountry", new YardAddress[0]));
        var yardAddress3 = await j.Fact(new YardAddress(yard, "myYardName3", "myRemark", "myStreet", "myHousNb", "myPostalCode", "myCity", "myCountry", new YardAddress[] { yardAddress1, yardAddress2 }));
        var yardAddress4 = await j.Fact(new YardAddress(yard, "myYardName3", "myRemark", "myStreet2", "myHousNb", "myPostalCode", "myCity", "myCountry", new YardAddress[] { yardAddress3 }));

        var lastRef = ReferenceOfFact(yardAddress4);

        var factGraph = await store.Load(ImmutableList<FactReference>.Empty.Add(lastRef), default);
        factGraph.FactReferences.Count.Should().Be(7);
    }

    [Fact]
    public async Task StoreRoundTripFromUTC()
    {
        DateTime now = DateTime.Parse("2021-07-04T01:39:43.241Z").ToUniversalTime();
        var j = GivenJinagaClient();
        var airlineDay = await j.Fact(new AirlineDay(new Airline("value"), now));
        airlineDay.date.Kind.Should().Be(DateTimeKind.Utc);
        airlineDay.date.Hour.Should().Be(1);
    }

    [Fact]
    public async Task SaveNormallyUploadsFact()
    {
        var store = fixture.CreateStore();
        var network = GivenLocalNetwork();
        var jinagaClient = GivenJinagaClient(store, network);
        await jinagaClient.Fact(new Airline("IA"));

        await jinagaClient.Unload();
        network.SavedFactReferences.Should().ContainSingle().Which
            .Type.Should().Be("Skylane.Airline");
    }

    [Fact]
    public async Task SaveLocallyDoesNotUploadFact()
    {
        var store = fixture.CreateStore();
        var network = GivenLocalNetwork();
        var jinagaClient = GivenJinagaClient(store, network);
        await jinagaClient.Local.Fact(new Airline("IA"));

        await jinagaClient.Unload();
        network.SavedFactReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveLocallyFollowedByPushDoesNotUploadFact()
    {
        var store = fixture.CreateStore();
        var network = GivenLocalNetwork();
        var jinagaClient = GivenJinagaClient(store, network);
        await jinagaClient.Local.Fact(new Airline("IA"));
        await jinagaClient.Push();

        await jinagaClient.Unload();
        network.SavedFactReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveLocallyFollowedBySaveSuccessorDoesUploadFact()
    {
        var store = fixture.CreateStore();
        var network = GivenLocalNetwork();
        var jinagaClient = GivenJinagaClient(store, network);
        var airline = await jinagaClient.Local.Fact(new Airline("IA"));
        await jinagaClient.Fact(new AirlineDay(airline, DateTime.Parse("2021-07-04").Date));

        await jinagaClient.Unload();
        network.SavedFactReferences.Count.Should().Be(2);
        network.SavedFactReferences.Should().Contain(r => r.Type == "Skylane.Airline");
        network.SavedFactReferences.Should().Contain(r => r.Type == "Skylane.Airline.Day");
    }

    private static FactReference ReferenceOfFact(object fact)
    {
        var store = new MemoryStore();
        var loggerFactory = NullLoggerFactory.Instance;
        var networkManager = new NetworkManager(new LocalNetwork(), store, loggerFactory, (FactGraph g, ImmutableList<Fact> l, CancellationToken c) => Task.CompletedTask);
        var factManager = new FactManager(store, networkManager, [], loggerFactory, 0);
        var graph = factManager.Serialize(fact);
        var lastRef = graph.Last;
        return lastRef;
    }

    private JinagaClient GivenJinagaClient(IStore? store = null, INetwork? network = null)
    {
        var options = new JinagaClientOptions();
        return new JinagaClient(store ?? fixture.CreateStore(), network ?? new LocalNetwork(), [], NullLoggerFactory.Instance, options);
    }

    private static LocalNetwork GivenLocalNetwork()
    {
        return new LocalNetwork();
    }
}
