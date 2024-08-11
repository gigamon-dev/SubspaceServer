using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.CallbackExamples;

// This example shows how to create your own custom callback and how to invoke it.

/// <summary>
/// A static helper class to assist with firing the Component Callback.
/// </summary>
public static class MyExampleCallback
{
    // Here is the delegate itself.
    //
    // It can be any delegate, 
    // and doesn't have to be nested in the class, 
    // but I've found it nice to just put it inside.
    // 
    // It's just a normal delegate, 
    // so it can have whatever signature you want.
    // Here's an example of one that takes with 3 parameters.
    public delegate void MyExampleDelegate(int foo, string bar, bool baz);

    // This is the helper method for registering.
    // It just wraps the call to the ComponentBroker.
    public static void Register(IComponentBroker broker, MyExampleDelegate handler)
    {
        broker?.RegisterCallback(handler);
    }

    // This is the helper method for unregistering.
    // It just wraps the call to the ComponentBroker.
    public static void Unregister(IComponentBroker broker, MyExampleDelegate handler)
    {
        broker?.UnregisterCallback(handler);
    }

    // This is the helper method for firing (invoking) the callback.
    // This is where the helper really shines.
    // It wraps the call to the ComponentBroker and 
    // a recursive call to the parent broker too.
    // That means if your broker was an Arena, then 
    // it will invoke the delegate on the arena-level first.
    // Next, it will go to the the parent, which is the root
    // broker, and do the same there.
    public static void Fire(IComponentBroker broker, int foo, string bar, bool baz)
    {
        // Invoke it on the broker.
        broker?.GetCallback<MyExampleDelegate>()?.Invoke(foo, bar, baz);

        // Recursively fire it on the parent of the broker (if there is a parent).
        if (broker?.Parent != null)
            Fire(broker.Parent, foo, bar, baz);
    }
}

public class CustomExample : IModule, IArenaAttachableModule
{
    bool IModule.Load(IComponentBroker broker)
    {
        // Fire a zone-wide Component Callback on the root broker.
        MyExampleCallback.Fire(broker, 123, "Hello entire zone!", true);
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        return true;
    }

    bool IArenaAttachableModule.AttachModule(Arena arena)
    {
        // Fire a Component Callback for a single arena..
        MyExampleCallback.Fire(arena, 123, "Hello single arena!", true);
        return true;
    }

    bool IArenaAttachableModule.DetachModule(Arena arena)
    {
        return true;
    }
}
