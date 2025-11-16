using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.CallbackExamples;

// This example shows how to create your own custom callback and how to invoke it.
// It uses a the source generator which writes methods to Register, Unregister, and Fire the callback.
// To use the source generator, add a reference to your plug-in project's .csproj file. It'll look like this:
// <ProjectReference Include="..\SourceGeneration\SourceGeneration.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
// Your path may differ based on where your plug-in project is in relation to the source generator project.
// The above is from the Example project which is on the same directory level as the SourceGeneration project.

/// <summary>
/// <para>
/// This is the delegate itself. It is an example of a delegate that takes with 3 parameters.
/// The delegate is just a regular delegate, so it can have whatever signature you want.
/// Callbacks normally do not return values, as they act like events, and therefore have a void return type.
/// The source generator expects the return type to be void.
/// Note: If you think you need a return type, you probably want to use an Advisor instead of a Callback.
/// </para>
/// <para>
/// The [ComponentCallback] attribute tells the source generator to generate the
/// <see cref="Register"/>, <see cref="Unregister"/>, and <see cref="Fire"/> methods.
/// Using the source generator is not necessary, but it helps write the methods for us.
/// </para>
/// </summary>
/// <param name="foo">The first, int parameter.</param>
/// <param name="bar">The second, string parameter.</param>
/// <param name="baz">The third, bool parameter.</param>
[ComponentCallback]
public delegate void MyExampleCallback(int foo, string bar, bool baz);

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
