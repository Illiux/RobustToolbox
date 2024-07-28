using System;
using System.Collections.Generic;
using Moq;
using Robust.Shared.GameObjects;

namespace Robust.UnitTesting.Shared.GameObjects;

public static class MockSubscriptionsExt
{
    public static void CaptureLocalSubscription<TComp, TEvent>(
        this Mock<EntitySystem.ISubscriptions> subs,
        ICollection<EntityEventRefHandler<TComp, TEvent>> handler)
        where TComp : IComponent
        where TEvent : notnull
    {
        subs.Setup(m => m.SubscribeLocalEvent(
            Capture.In(handler),
            It.IsAny<Type[]>(),
            It.IsAny<Type[]>())
        );
    }

}
