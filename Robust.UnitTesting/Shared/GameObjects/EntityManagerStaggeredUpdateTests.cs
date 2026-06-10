using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using Robust.Shared.GameObjects;
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
        _random = new Mock<IRobustRandom>();
        _timing = new GameTiming { TickRate = TickRate };
        _updateTracker = CreateUpdateTracker();
    }

    [TearDown]
    public void After()
    {
        _components.Clear();
        _paused.Clear();
    }

    [Test]
    public void TestRemovedComponentsAreUntracked()
    {
        var entity = new EntityUid(1);
        var comp = CreateComponent(entity);

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
        var comp = CreateComponent(entity);

        _components.Remove(entity);
        _timing.CurTick += 1;
        Assert.That(_updateTracker.ToList(), Is.Empty);

        _components.Add(entity, comp);
        MapInit(entity, comp);
        _timing.CurTick += _timing.TickRate;
        Assert.That(_updateTracker.ToList(), Has.Exactly(1).EqualTo((entity, comp)));
    }

    [Test]
    public void TestRegularUpdate()
    {
        var entity = new EntityUid(1);
        SetRandomOffset(1);
        var comp = CreateComponent(entity);

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

    [Test]
    public void TestPausedEntityIsSkippedUntilNextInterval()
    {
        var entity = new EntityUid(1);
        SetRandomOffset(0);
        var comp = CreateComponent(entity);

        _paused.Add(entity);
        _timing.CurTick += 1;
        Assert.That(
            _updateTracker.ToList(),
            Is.Empty,
            "Paused entities do not update even when their scheduled time has elapsed");

        _paused.Remove(entity);
        _timing.CurTick += (byte)(_timing.TickRate - 1);
        Assert.That(
            _updateTracker.ToList(),
            Is.Empty,
            "The skipped update is not replayed immediately when the entity is unpaused");

        _timing.CurTick += 1;
        Assert.That(
            _updateTracker.ToList(),
            Contains.Item((entity, comp)),
            "The entity updates again on its next scheduled interval after being unpaused");
    }

    [Test]
    public void TestDifferentRandomOffsetsUpdateIndependently()
    {
        var earlyEntity = new EntityUid(1);
        var lateEntity = new EntityUid(2);

        _random.SetupSequence(m => m.Next(TimeSpan.Zero, It.IsAny<TimeSpan>()))
            .Returns(_timing.TickPeriod.Mul(0))
            .Returns(_timing.TickPeriod.Mul(5));

        var earlyComp = CreateComponent(earlyEntity);
        var lateComp = CreateComponent(lateEntity);

        _timing.CurTick += 1;
        Assert.That(_updateTracker.ToList(), Is.EqualTo([(earlyEntity, earlyComp)]));

        _timing.CurTick += 4;
        Assert.That(
            _updateTracker.ToList(),
            Is.Empty,
            "The later entity should not update before its own randomized offset has elapsed");

        _timing.CurTick += 1;
        Assert.That(_updateTracker.ToList(), Is.EqualTo([(lateEntity, lateComp)]));
    }

    [Test]
    public void TestChainedMapInitHandlerIsInvoked()
    {
        Entity<StaggeredUpdateComponent>? eventEntity = null;
        var mapInitCalls = 0;
        _updateTracker = CreateUpdateTracker(
            (ent, ref _) =>
            {
                eventEntity = ent;
                mapInitCalls++;
            });

        var entity = new EntityUid(1);
        var comp = CreateComponent(entity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapInitCalls, Is.EqualTo(1));
            Assert.That(eventEntity, Is.EqualTo(WrapEnt(entity, comp)));
        }
    }

    private void SetRandomOffset(int ticks)
    {
        _random.Setup(m => m.Next(TimeSpan.Zero, It.IsAny<TimeSpan>()))
            .Returns(_timing.TickPeriod.Mul(ticks));
    }

    private StaggeredUpdateTracker<StaggeredUpdateComponent> CreateUpdateTracker(
        EntityEventRefHandler<StaggeredUpdateComponent, MapInitEvent>? chainedHandler = null)
    {
        List<EntityEventRefHandler<StaggeredUpdateComponent, MapInitEvent>> onMapInit = [];

        var subs = new Mock<EntitySystem.ISubscriptions>();
        subs.CaptureLocalSubscription(onMapInit);

        var manager = new Mock<IEntityManager>();
        var query = new EntityQuery<StaggeredUpdateComponent>(null, _components);
        manager.Setup(m => m.IsPaused(It.IsAny<EntityUid>(), It.IsAny<MetaDataComponent>()))
            .Returns((EntityUid uid, MetaDataComponent _) => _paused.Contains(uid));
        manager.Setup(m => m.GetEntityQuery<StaggeredUpdateComponent>()).Returns(query);

        var tracker = new StaggeredUpdateTracker<StaggeredUpdateComponent>(
            chainedHandler,
            subs.Object,
            manager.Object,
            _random.Object,
            _timing);

        _onMapInit = onMapInit[0];
        return tracker;
    }

    private StaggeredUpdateComponent CreateComponent(EntityUid entity)
    {
        var comp = new StaggeredUpdateComponent();
        _components.Add(entity, comp);
        MapInit(entity, comp);
        return comp;
    }

    private void MapInit(EntityUid entity, StaggeredUpdateComponent comp)
    {
        _onMapInit.Invoke(WrapEnt(entity, comp), ref _mapInitEventInstance);
    }

    private Entity<StaggeredUpdateComponent> WrapEnt(EntityUid entity, StaggeredUpdateComponent comp)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        comp.Owner = entity; // have to set this to pass AssertOwner check
#pragma warning restore CS0618 // Type or member is obsolete

        return new Entity<StaggeredUpdateComponent>(entity, comp);
    }
}
