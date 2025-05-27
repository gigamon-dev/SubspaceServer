using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.CallbackExamples;

// This example shows how to create your own custom callback and how to invoke it.

/// <summary>
/// A static helper class to assist with firing the Component Callback.
/// It uses a source generator to generate the <see cref="Register"/>, <see cref="Unregister"/>, and <see cref="Fire"/> methods.
/// Using the source generator is not necessary, but it helps write the methods for us.
/// <para>
/// To use the source generator the class:
/// <list type="bullet">
/// <item>is decorated with the <see cref="CallbackHelperAttribute"/></item>
/// <item>is given the <see langword="partial"/> modifier so that the source generator can add methods for us</item>
/// <item>is given a name ending with "Callback" (required by the source generator)</item>
/// </list>
/// </para>
/// </summary>
[CallbackHelper]
public static partial class MyExampleCallback
{
    // Here is the delegate itself.
    // Since we're using the source generator, the delegate must be named after the class and be public.
    // So, since the class is named MyExampleCallback, the delegate is named MyExampleDelegate.
    // The source generator expects this naming convention. If not followed, it will not generate code.
    // 
    // The delegate is just a regular delegate, so it can have whatever signature you want.
    // Callbacks normally do not return values, as they act like events, and therefore have a void return type.
    // The source generator expects the return type to be void.
    // Note: If you think you need a return type, you probably want to use an Advisor instead of a Callback.
    //
    // Here's an example of a delegate that takes with 3 parameters.
    public delegate void MyExampleDelegate(int foo, string bar, bool baz);
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
