namespace Robust.Shared.GameObjects;

public partial interface IEntityManager
{
    StaggeredUpdateTracker<TComp> GetStaggeredUpdateTracker<TComp>( EntityEventRefHandler<TComp, MapInitEvent>? mapInit)
        where TComp : IComponent, IStaggeredUpdate;
}
