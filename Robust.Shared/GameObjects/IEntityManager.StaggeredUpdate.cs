namespace Robust.Shared.GameObjects;

public partial interface IEntityManager
{
    StaggeredUpdateTracker<TComp> GetStaggeredUpdateTracker<TComp>(
        EntityEventRefHandler<TComp, MapInitEvent>? mapInit,
        EntitySystem.ISubscriptions subs
    ) where TComp : IComponent, IStaggeredUpdate;
}
