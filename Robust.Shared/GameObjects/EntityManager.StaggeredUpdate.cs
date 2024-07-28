using System;
using System.Collections;
using Robust.Shared.Collections;
using System.Collections.Generic;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Shared.GameObjects;

public partial class EntityManager
{
    [IoC.Dependency] private readonly IGameTiming _timing = default!;
    [IoC.Dependency] private readonly IRobustRandom _rng = default!;

    public StaggeredUpdateTracker<TComp> GetStaggeredUpdateTracker<TComp>(
        EntitySystem.ISubscriptions subs
        ) where TComp : IComponent, IStaggeredUpdate
    {
        return new StaggeredUpdateTracker<TComp>(subs, GetEntityQuery<TComp>(), _rng, _timing);
    }
}

public interface IStaggeredUpdate
{
    TimeSpan UpdateInterval { get; }
}

public sealed class StaggeredUpdateTracker<TComp> : IEnumerable<(EntityUid entity, TComp comp)>
    where TComp : IComponent, IStaggeredUpdate
{
    private readonly PriorityQueue<EntityUid, TimeSpan> _insertQueue = new();
    private readonly RingBufferList<(EntityUid entity, TimeSpan when)> _schedule = [];
    private readonly HashSet<EntityUid> _tracked = [];

    private readonly EntityQuery<TComp> _query;
    private readonly IRobustRandom _rng;
    private readonly IGameTiming _timing;
    private readonly TimeSpan _updateInterval;

    public EntityEventRefHandler<TComp, MapInitEvent>? MapInit;

    internal StaggeredUpdateTracker(
        TimeSpan updateInterval,
        EntitySystem.ISubscriptions subs,
        EntityQuery<TComp> query,
        IRobustRandom rng,
        IGameTiming timing)
    {
        _query = query;
        _rng = rng;
        _timing = timing;

        subs.SubscribeLocalEvent<TComp, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<TComp> ent, ref MapInitEvent args)
    {
        if (!_tracked.Add(ent.Owner)) return;

        // randomize an offset from the current tick, up to interval
        // we start from current tick + 1 because updates for the current tick may already have been processed
        var when = _timing.CurTime + _timing.TickPeriod + _rng.Next(TimeSpan.Zero, ent.Comp.UpdateInterval);
        _insertQueue.Enqueue(ent.Owner, when);

        MapInit?.Invoke(ent, ref args); // call a chained event handler if we have one
    }

    public IEnumerator<(EntityUid, TComp)> GetEnumerator()
    {
        return new Enumerator(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private struct Enumerator(StaggeredUpdateTracker<TComp> tracker) : IEnumerator<(EntityUid entity, TComp comp)>
    {
        private readonly TimeSpan _until = tracker._timing.CurTime;

        public (EntityUid entity, TComp comp) Current { get; private set; }
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (TryExtract(out var entity, out var when))
            {
                // since we only schedule when the component can be resolved, entities where the component has been
                // deleted are dropped from the tracker
                if (tracker._query.TryComp(entity, out var comp))
                {
                    tracker._schedule.Add((entity, when + comp.UpdateInterval));

                    // If it's not paused, yield it from the enumerator
                    Current = (entity, comp);
                    return true;
                }

                // if our component is missing, stop tracking this entity
                tracker._tracked.Remove(entity);
            }

            return false;
        }

        private bool TryExtract(out EntityUid entity, out TimeSpan when)
        {
            // the next entity may come either from the insertion list or the schedule, whichever is sooner
            var queueWhen = tracker._insertQueue.TryPeek(out var queueEnt, out var w)
                ? w
                : TimeSpan.MaxValue;
            var (schedEnt, schedWhen) = tracker._schedule.TryGetValue(0, out var s)
                ? s
                : (default, TimeSpan.MaxValue);

            if (schedWhen > _until && queueWhen > _until)
            {
                entity = default;
                when = TimeSpan.MaxValue;
                return false;
            }

            if (queueWhen < schedWhen)
            {
                tracker._insertQueue.Dequeue();
                entity = queueEnt;
                when = queueWhen;
                return true;
            }

            tracker._schedule.RemoveAt(0);
            entity = schedEnt;
            when = schedWhen;
            return true;
        }

        public void Reset()
        {
            throw new NotSupportedException();
        }

        void IDisposable.Dispose()
        {
        }
    }
}
