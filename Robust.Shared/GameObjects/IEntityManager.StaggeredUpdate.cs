using System.Collections.Generic;

namespace Robust.Shared.GameObjects;

public partial interface IEntityManager
{
    StaggeredUpdateTracker<TComp> GetStaggeredUpdateTracker<TComp>(
        EntitySystem.ISubscriptions subs) where TComp : IComponent, IStaggeredUpdate;
}
