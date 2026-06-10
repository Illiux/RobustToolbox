using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Random;
using Robust.Shared.Reflection;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.UnitTesting.Shared.GameObjects;

[Reflect(false)]
internal sealed partial class StaggeredUpdateComponent : Component, IStaggeredUpdate
{
    public static TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);
}

public sealed class EntityManagerStaggeredUpdateIntegration : RobustIntegrationTest
{
    [Reflect(false)]
    private sealed class TestStaggeredUpdateSystem : EntitySystem
    {
        private StaggeredUpdateTracker<StaggeredUpdateComponent> _tracker = null!;

        public override void Initialize()
        {
            _tracker = GetStaggeredUpdateTracker<StaggeredUpdateComponent>();
        }

        public override void Update(float frameTime)
        {
            throw new NotImplementedException();
        }
    }

    [Test]
    public async Task Test()
    {
        var options = new ServerIntegrationOptions { Pool = false };
        options.BeforeRegisterComponents += () =>
        {
            var fact = IoCManager.Resolve<IComponentFactory>();
            fact.RegisterClass<StaggeredUpdateComponent>();
        };
        options.BeforeStart += () =>
        {
            var sysMan = IoCManager.Resolve<IEntitySystemManager>();
            sysMan.LoadExtraSystemType<TestStaggeredUpdateSystem>();
        };

        var server = StartServer(options);
        await server.WaitIdleAsync();

        await server.WaitRunTicks(1);
    }
}


[TestFixture, Parallelizable, TestOf(typeof(EntityManager))]
public sealed class EntityManagerStaggeredUpdateUnit
{
    private const int TickRate = 10;
    private static MapInitEvent _mapInitEventInstance = new();

    private readonly Dictionary<EntityUid, IComponent> _components = [];
    private readonly HashSet<EntityUid> _paused = [];

    private EntityEventRefHandler<StaggeredUpdateComponent, MapInitEvent> _onMapInit = null!;
    private StaggeredUpdateTracker<StaggeredUpdateComponent> _updateTracker = null!;
    private Mock<IRobustRandom> _random = null!;
    private GameTiming _timing = null!;

    [SetUp]
    public void Before()
    {
        List<EntityEventRefHandler<StaggeredUpdateComponent, MapInitEvent>> onMapInit = [];

        var subs = new Mock<EntitySystem.ISubscriptions>();
        subs.CaptureLocalSubscription(onMapInit);


        var manager = new Mock<IEntityManager>();
        var query = new EntityQuery<StaggeredUpdateComponent>(null, _components);
        manager.Setup(m => m.IsPaused(It.IsAny<EntityUid>(), It.IsAny<MetaDataComponent>()))
            .Returns((EntityUid uid, MetaDataComponent _) => _paused.Contains(uid));
        manager.Setup(m => m.GetEntityQuery<StaggeredUpdateComponent>()).Returns(query);

        _random = new Mock<IRobustRandom>();
        _timing = new GameTiming { TickRate = TickRate };
        _updateTracker = new StaggeredUpdateTracker<StaggeredUpdateComponent>(
            null,
            subs.Object,
            manager.Object,
            _random.Object,
            _timing);

        _onMapInit = onMapInit[0];
    }

    [TearDown]
    public void After()
    {
        _components.Clear();
    }

    [Test]
    public void TestRemovedComponentsAreUntracked()
    {
        var entity = new EntityUid(1);
        var comp = new StaggeredUpdateComponent();
        _components.Add(entity, comp);
        _onMapInit.Invoke(WrapEnt(entity, comp), ref _mapInitEventInstance);

        _timing.CurTick += _timing.TickRate;
        Assert.That(_updateTracker.ToList(), Contains.Item((entity, comp)));

        _timing.CurTick += _timing.TickRate;
        _components.Remove(entity);
        Assert.That(_updateTracker.ToList(), Is.Empty);

        _timing.CurTick += _timing.TickRate;
        _components.Add(entity, comp);
        Assert.That(
            _updateTracker.ToList(),
            Is.Empty,
            "Do not return the entity, even when the component is added back");
    }

    [Test]
    public void TestDoubleAdd()
    {
        var entity = new EntityUid(1);
        var comp = new StaggeredUpdateComponent();
        _components.Add(entity, comp);
        _onMapInit.Invoke(WrapEnt(entity, comp), ref _mapInitEventInstance);

        _components.Remove(entity);
        _timing.CurTick += 1;
        Assert.That(_updateTracker.ToList(), Is.Empty);

        _components.Add(entity, comp);
        _onMapInit.Invoke(WrapEnt(entity, comp), ref _mapInitEventInstance);
        _timing.CurTick += _timing.TickRate;
        Assert.That(_updateTracker.ToList(), Has.Exactly(1).EqualTo((entity, comp)));
    }

    [Test]
    public void TestRegularUpdate()
    {
        var entity = new EntityUid(1);
        var comp = new StaggeredUpdateComponent();
        _components.Add(entity, comp);
        SetRandomOffset(1);
        _onMapInit.Invoke(WrapEnt(entity, comp), ref _mapInitEventInstance);

        _timing.CurTick += 1;
        Assert.That(_updateTracker.ToList(), Is.Empty);

        _timing.CurTick += 1;
        Assert.That(
            _updateTracker.ToList(),
            Contains.Item((entity, comp)),
            "Update after exactly offset + 1 ticks");

        _timing.CurTick += (byte)(_timing.TickRate - 1);
        Assert.That(
            _updateTracker.ToList(),
            Is.Empty,
            "Only return entity once until time is advanced by a full second");

        _timing.CurTick += 1;
        Assert.That(
            _updateTracker.ToList(),
            Contains.Item((entity, comp)),
            "Update exactly one second after previous update");
    }

    private void SetRandomOffset(int ticks)
    {
        _random.Setup(m => m.Next(TimeSpan.Zero, It.IsAny<TimeSpan>()))
            .Returns(_timing.TickPeriod.Mul(ticks));
    }

    private Entity<StaggeredUpdateComponent> WrapEnt(EntityUid entity, StaggeredUpdateComponent comp)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        comp.Owner = entity; // have to set this to pass AssertOwner check
#pragma warning restore CS0618 // Type or member is obsolete

        return new Entity<StaggeredUpdateComponent>(entity, comp);
    }
}
