# Subspace Server .NET - Developer Guide

The modular design that Subspace Server .NET uses mirrors that of ASSS and as such is meant to be completely customizable and extendable. Yes, you can always modify the core server itself by cloning the repository and making your own changes. However, more likely you will want to add your own custom functionality by writing modules that plug-in to the server. This document is to provide some guidance on how to do just that.

> For those familiar with ASSS, writing a module for Subspace Server .NET should be a walk in the park. All of the same concepts apply, the only difference is that you'll be using features built into the C# language rather than all the macro magic ASSS uses.

## Modules

The basic building block of the server is a server module. The server itself consists of many modules that are working together in unison. A module is simply just a class that the server creates an instance of and calls `Load` and `Unload` methods on.

> **Tip:** When this guide mentions the term *module*, it's referring to server modules, the mechanism to extend the server. Don't confuse this with  a *.NET module*, which is a completely separate concept.

### Startup

When the server starts up it reads a configuration file in the "conf" folder ("conf/Modules.config"). This file lists all of the modules that need to be loaded, in the order that they should be loaded. The order matters! Modules loaded later normally will have dependencies on parts from modules loader earlier. 

> **Fun fact:** The part that loads modules, `SS.Core.Modules.ModuleLoader`, is actually a module too. The only difference is that it's not loaded dynamically since it's one that reads the Modules.config file.

In the Module.config file, there are 2 variations.  One is for built-in modules, that is, modules that come as part of the server.  It looks like:
```xml
<module type="SS.Core.Modules.Prng, SS.Core" />
```
That tells the server to load the `Prng` (Pseudo-random number generator module) which is a built-in module in the SS.Core assembly.

The other form is for modules in separate plug-in assemblies. In other words, this is what you would use to extend functionality. Here's an example of what loading a plug-in module looks like:
```xml
<module type="SS.Replay.ReplayModule" path="bin/modules/Replay/SS.Replay.dll" />
```
Notice that it doesn't include the assembly name in the *type* attribute. Rather, it has a *path* attribute which contains the path of the assembly to load.  Also notice the path is within "bin/modules". That is where you put your own plug-in assemblies containing your custom modules.

> **Tip:** A list of loaded modules can be listed in-game using the `?lsmod` command.

### How to create a plugin project for a module

> **Tip:** For those new to .NET plugins, it is recommended you read the [.NET documentation](https://docs.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support) about it.

The examples shown in this tutorial are included in the repository, see the [Example folder](../src/Example/). Also, other examples of plugins include the [Replay module](../src/Replay/Replay.csproj) and the [MatchMaking module](../src/Matchmaking/Matchmaking.csproj).

First, create a new class library project and add a reference the [Core](../src/Core/Core.csproj) asssembly

Modify the .csproj file to tell the build process to build it in such a way that it can be used a plugin. Here's what you'll be changing:

1. Enable dynamic loading
2. (optional but recommended) Change the OutDir to place built files into the modules folder.
3. Edit references to prevent dependencies from being copied to the output folder.

In the .csproj file, add the:
```XML
<EnableDynamicLoading>true</EnableDynamicLoading>
```
element between the `<PropertyGroup>` tags.

Next, it's recommended to change the output directory so that built files get placed under the Zone's bin\modules folder. To do this, add the `<OutDir>` element and set it to a path within the modules folder.

Edit the reference to the Core assembly to tell the build process to not copy the SS.Core.dll or any libraries it depends on to the output directory. Add the `<Private>false</Private>` and `<ExcludeAssets>all</ExcludeAssets>` elements to the `<ProjectReference>`. These changes are very imporant! If SS.Core.dll was copied, a second copy of SS.Core.dll would get loaded and the server would fail to find the dependencies for your module.

Here's what the .csproj file should like after the changes are made:
```XML
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <OutDir>$(SolutionDir)SubspaceServer\Zone\bin\modules\Example</OutDir>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Core\Core.csproj">
	  <Private>false</Private>
	  <ExcludeAssets>all</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>

</Project>
```

> View the [example .csproj file](/src/Example/Example.csproj).

### How to create a module

To create a module, simply create a class and have it implement the `SS.Core.IModule` interface. Here's what it should look like:

```C#
/// <summary>
/// This is an example of the simplest form of loading and unloading a module.
/// </summary>
public sealed class ExampleModule : IModule
{
    bool IModule.Load(IComponentBroker broker)
    {
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        return true;
    }
}
```
> View the [example code](/src/Example/ModuleLifeCycleExamples/ExampleModule.cs)

Both the `Load` and `Unload` methods return a `bool`, which indicates success (`true`) or failure (`false`). Also, both have a parameter of type `IComponentBroker`. More on that later (see the [ComponentBroker](#ComponentBroker) section below).

As you may have guessed from the name, `Load` is called when the server wants to load the module. This normally happens during the [startup](#startup) process mentioned earlier. Likewise, the `Unload` method is called when the server wants to unload the module. Unloading normally happens when the server is shutting down or restarting.

To load the `ExampleModule` module on startup, you'd simply edit the Modules.config with:
```xml
<module type="Example.ModuleLifeCycleExamples.ExampleModule" path="bin/modules/Example/Example.dll" />
```
That is, you want the server to create an instance of the `ExampleModule` class in the  `Example.ModuleLifeCycleExamples` namespace of your `Example.dll` assembly which is in the "bin/modules/Example" folder.

> **Fun fact:** Modules can also be manually loaded or unloaded using in-game commands <nobr>`?insmod`</nobr> and <nobr>`?rmmod`</nobr> respectively.

## Module life-cycle

There are many steps in a module's life-cycle that can be hooked into. The `Load` and `Unload` steps are the obvious, required steps. Here's an overview of the steps that are available and what interface provides the hook in.

| # | Step | Synchronous Method | Asynchronous Method |
| --- | --- | --- | --- |
| 1. | Load | `IModule.Load` | `IAsyncModule.LoadAsync` |
| 2. | *(optional)* Post-Load | `IModuleLoaderAware.PostLoad` | `IAsyncModuleLoaderAware.PostLoadAsync` |
| 3. | *(optional)* Attach to Arena ^ | `IArenaAttachableModule.AttachModule` | `IAsyncArenaAttachableModule.AttachModuleAsync` |
| 4. |*(optional)* Detach from Arena ^ | `IArenaAttachableModule.DetachModule` | `IAsyncArenaAttachableModule.DetachModuleAsync` |
| 5. | *(optional)* Pre-Unload | `IModuleLoaderAware.PreUnload` | `IAsyncModuleLoaderAware.PreUnloadAsync` |
| 6. | Unload | `IModule.Unload` | `IAsyncModule.UnloadAsync` |

^ For as many arenas as the module is configured to be attached to.

Notice that there are synchronous/asynchronous pairs of interfaces that provide equivalent hooks into the module life-cycle:

- `IModule` / `IAsyncModule`
- `IModuleLoaderAware` / `IAsyncModuleLoaderAware`
- `IArenaAttachableModule` / `IAsyncArenaAttachableModule`

The asynchronous versions are there in case there is a need to do asynchronous work such as: 
- Accessing a database
- Accessing a web service
- Accessing a file
- Opening a .conf file (`IConfigManager.OpenConfigFile`)
- etc...

> If for some strange reason both the synchronous and asynchronous interfaces are implemented, the server will use the asynchronous interface. 

### `IModule` / `IAsyncModule`

Loading and unloading a module is a necessity. Therefore, a module must implement either `IModule` or `IAsyncModule`.

### `IModuleLoaderAware` / `IAsyncModuleLoaderAware`
The `IModuleLoaderAware` and `IAsyncModuleLoaderAware` interfaces provide a mechanism that allows tying into additional steps of loading and unloading. The **PostLoad** step is done during startup after all modules listed in the *Modules.config* file have been loaded. Likewise, **PreUnload** is an additional step called during shutdown, which happens before the `Unload` method of any module is called.

> **Tip:** Modules can be manually loaded and unloaded using in-game commands `?insmod` and `?rmmod` respectively. This obviously happens after the initial module loading process, so any module that implements `IModuleLoaderAware` or `IAsyncModuleLoaderAware` and is manually loaded with `?insmod` will be immediately be **PostLoad**ed. Likewise, any module that is manually unloaded with `?rmmod` will be **PreUnload**ed.

Here's an example of hooking into the PostLoad and PreUnload steps:
```C#
/// <summary>
/// This is an example of hooking into the PostLoad and PreUnload steps of the module life-cycle.
/// </summary>
public sealed class LoaderAwareExample : IModule, IModuleLoaderAware
{
    public bool Load(IComponentBroker broker)
    {
        return true;
    }

    public void PostLoad(IComponentBroker broker)
    {
        // Do something after all modules have been loaded.
    }

    public void PreUnload(IComponentBroker broker)
    {
        // Do something before all modules are to be unloaded.
    }

    public bool Unload(IComponentBroker broker)
    {
        return true;
    }
}
```

> View the [example code](/src/Example/ModuleLifeCycleExamples/LoaderAwareExample.cs)


### `IArenaAttachableModule` / `IAsyncArenaAttachableModule`
The `IArenaAttachableModule` and `IAsyncArenaAttachableModule` interfaces provide a mechanism to perform tasks for specific arenas that they are configured for, rather than server-wide (all arenas). It can be used when you only want your module to affect certain arenas. There is an arena.conf setting `Modules:AttachModules` which allows you to specify the modules that should be attached. Here's an example of what that setting might look like:
```ini
; This is in an arena.conf
[ Modules ]
AttachModules = \
	Example.AnAttachableModule \
	Example.AnotherAttachableModule
```
This tells the server to look for the `Example.AnAttachableModule` and `Example.AnotherAttachableModule`, and call the appropriate `IArenaAttachableModule.AttachModule` and `IArenaAttachableModule.DetachModule` methods when the arena is created or destroyed.

> **Tip:** Modules can also be manually attached or detached from an arena using in-game commands <nobr>`?attmod`</nobr> and <nobr>`?detmod`</nobr> respectively.

Here's an example of hooking into the *Attach to Arena* and *Detach from Arena* steps:

```C#
/// <summary>
/// This is an example of hooking into the AttachModule and DetachModule steps of the module life-cycle.
/// </summary>
public sealed class ArenaAttachableExample : IModule, IArenaAttachableModule
{
    public bool Load(IComponentBroker broker)
    {
        return true;
    }

    public bool AttachModule(Arena arena)
    {
        // Do something specifically for the arena.
        return true;
    }

    public bool DetachModule(Arena arena)
    {
        // Do something specifically for the arena.
        return true;
    }

    public bool Unload(IComponentBroker broker)
    {
        return true;
    }
}
```

> View the [example code](/src/Example/ModuleLifeCycleExamples/ArenaAttachableExample.cs)

## ComponentBroker
Now that you know how to create a module and get it to load, you'll want your module to talk to other parts of the the server. This is where the `ComponentBroker` comes in. The `ComponentBroker` acts as an intermediary providing services for modules to interact with one another. That is, modules use a `ComponentBroker` to discover parts from other modules and to expose parts of themselves to other modules. A `ComponentBroker` provides three mechanisms for modules to communicate with one another: [Component Interfaces](#component-interfacesinterfaces), [Component Callbacks](#component-callbackscallbacks), and [Component Advisors](#component-advisorsdvisors). 

> **Note:** In this document I'm referring to Interfaces, Callbacks, and Advisors, with "Component" in their names to be less ambiguious and show that they are meant to be used the `ComponentBroker`. Also, "Components" are usually modules, but that doesn't have to be the case. It is completely possible for a module to pass a `ComponentBroker` to another part which is not a module.

A `ComponentBroker` acts like a container. There is one root `ComponentBroker` which represents the global scope. In other words, server-wide / zone-wide. Next, there are arenas. An `Arena` is a `ComponentBroker`, with the root `ComponentBroker` being its parent. In other words, there's a single root, with each `Arena` as a child leaf. It will make more sense later why this tree structure exists. For now, just know that it exists and that it's all about controlling scope.

> In ASSS, the equivalent of the `ComponentBroker` is the module manager, exposed through the `Imodman` interface.

> ***Fun fact:*** In Subspace Server .NET, there also is a `ModuleManager` and it happens to be the 'root' `ComponentBroker`.

Remember, the `Load` and `Unload` methods? Their first parameter is an `IComponentBroker`.  It's the 'root' `ComponentBroker` that gets passed in.

## Component Interfaces
Component Interfaces are exactly what they sound like, they're normal C# interfaces that a component can register on a `ComponentBroker` for others to find and use. To help distinguish any interface from a component interface, they derive from `SS.Core.IComponentInterface`.

Normally, there is only one implementation of an Component Interface. However, it is possible for multiple modules to each register an instance for the same ComponentInterface. In this case, the last one registered becomes the 'current' implementation, effectively overriding the previous. Authentication modules which implement the `IAuth` Component Interface use this feature to chain authentication logic on top of each other. That is, one authentication module gets the prior implementation before overriding it. Then, when and if it needs to, it can fail over and call the original implementation.

> Also, you don't need to know this but, you might notice it. When registering an interface, there is an optional parameter for which you can specify a name. This provides a way to differente between multiple registered instances. This functionality is only in a very special case, by encryption modules.

### Getting a Component Interface

First and foremost, you'll probably want to access the interfaces of other parts that are built into the server. So you'll need to know which interface you want. You can find the available built-in interfaces in the [SS.Core.ComponentInterfaces](../src/Core/ComponentInterfaces) namespace. Also, for a listing see the [ASSS Equivalents](asss-equivalents.md) document.

Getting the currently registered instance implementing a Component Interface can be done in two ways:
- Inject required Component Interface dependencies into your module's constructor.
- Manually get an interface using `IComponentBroker`.

#### **Injection**
Dependencies that are **required** can be injected into a module's constructor. The server will only be able to call the constructor if it can find **all** of the required dependencies. Optional, interface dependencies should use the Manual method.

> **Fun fact:** The Component Interface functionality of the `ComponentBroker` is a form of *service locator*. The injection of Component Interfaces into a module's constructor is a form of *dependency injection* that is performed by the `ModuleManager`. Together, they provide a form of *Inversion of Control (IoC)*.

Here is an example of using injection:

```C#
/// <summary>
/// An example on how to inject a component interface dependency into a constructor.
/// </summary>
public sealed class InjectionExample : IModule
{
    private readonly ILogManager _logManager;

    // Here we declare ILogManager as being a required dependency.
    public InjectionExample(ILogManager logManager)
    {
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
    }

    bool IModule.Load(IComponentBroker broker)
    {
        // Use it.
        _logManager.LogM(LogLevel.Info, nameof(InjectionExample), "Subspace Server .NET is awesome!");

        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        // For the injected component interfaces, 
        // getting the interface is done for you, and
        // releasing it is too. There's nothing to do.
        return true;
    }
}
```

> View the full example code at: [InjectionExample](../src/Example/InterfaceExamples/InjectionExample.cs)

#### **Manually**
Component Interfaces can be manually gotten using the `GetInterface` method of `ComponentBroker`. It will return a reference to the currently registered instance, or null if not found. Getting an Component Interface manually is done when it's an optional dependency. That is, your module can still work without it.

>**IMPORTANT**:  The `ComponentBroker` keeps track of a reference count for Component Interfaces. `GetInterface` increments the count and `ReleaseInterface` decrements the count. Each call to `GetInterface` should have a corresponding `ReleaseInterface`. Failure to do so will prevent modules from unloading because they'll think still being used.

Here's an example of manually getting a Component Interface:

```C#
/// <summary>
/// This example shows a manually gotten Component Interface
/// that is used for the entire life of the module.
/// </summary>
public sealed class ManualExample : IModule
{
    private ILogManager? _logManager;

    bool IModule.Load(IComponentBroker broker)
    {
        // You can hold onto the reference,
        // but you must release it at some point.
        _logManager = broker.GetInterface<ILogManager>();

        // Use it, only if it was available.
        // Keep in mind, GetInterface could have returned null.
        // Therefore, checking for null with the ?. (null-conditional operator)
        _logManager?.LogM(LogLevel.Info, nameof(ManualExample), "Subspace Server .NET is awesome!");

        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        // Manually gotten Component Interfaces must be manually released.
        // This is necessary!
        // If we had forgotten to do it, the LogManager module would not Unload.
        if (_logManager is not null)
            broker.ReleaseInterface(ref _logManager);

        return true;
    }
}
```

> View the full example code at: [ManualExample](../src/Example/InterfaceExamples/ManualExample.cs)

Here's another variation of manually getting an interface and releasing it, but this one does not hold onto the interface. Additionally, it shows how you can use a primary constructor to inject the IComponentBroker.
```C#
/// <summary>
/// This is an example that shows getting a Component Interface
/// for a short period, using it, and releasing it when done.
/// </summary>
public sealed class ManualExample2(IComponentBroker broker) : IModule
{
    private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    bool IModule.Load(IComponentBroker broker)
    {
        LogSomething("Hello Subspace!");
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        return true;
    }

    public void LogSomething(string message)
    {
        // Try to get it.
        ILogManager? logManager = _broker.GetInterface<ILogManager>();

        // Check whether it was available.
        if (logManager is not null)
        {
            // It was available.
            try
            {
                // Use it.
                logManager.LogM(LogLevel.Info, nameof(ManualExample2), message);
            }
            finally
            {
                // Release it when done.
                _broker.ReleaseInterface(ref logManager);
            }
        }
    }
}
```

> View the full example code at: [ManualExample2](../src/Example/InterfaceExamples/ManualExample2.cs)

### Registering and Unregistering a Component Interface

To create a Component Interface, simply create a regular interface and have it inherit from `IComponentInterface`.

```C#
using SS.Core;

public interface IMyExample : IComponentInterface
{
    // It's a normal C# interface, so you can include 
    // any members your want to expose to others.
    // These are usually methods, but is not limited to that.
    // For example, the IPlayerData interface has a property
    // to access the collection of Players.

    // Here's an example of exposing a method that we'll use later.
    void DoSomething();
}
```

Next, implement the interface. Normally, the module itself will implement the interface. Next, it needs to be registered for other parts to discover and access it. Call the `RegisterInterface` method on the `ComponentBroker`, passing in the instance that implements the interface. This returns a token which can later be used to unregister the interface by calling `UnregisterInterface`. This token ensures that only the one that registered the interface (has the token) can unregister it.

Here's an example of registering an interface in the `Load` method and unregistering it in the `Unload` method:
```C#
public sealed class RegistrationExample : IModule, IMyExample
{
    private InterfaceRegistrationToken<IMyExample>? _iMyExampleToken;

    bool IModule.Load(IComponentBroker broker)
    {
        // Register the interface.
        // This is normally done at the end of Load, when everything is initialized and ready.
        // Notice the return value is a token that we'll later use to unregister.
        _iMyExampleToken = broker.RegisterInterface<IMyExample>(this);

        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        // Unregister the interface.
        // This is normally the first thing done in Unload.
        if (broker.UnregisterInterface(ref _iMyExampleToken) != 0)
            return false;

        // Do other cleanup now that others should no longer be accessing us.
        // ...

        return true;
    }

    // Here the interface is explicitly implemented, 
    // but it doesn't need to be explicit. It's up to you.
    void IMyExample.DoSomething()
    {
        // Do some action here that you wanted exposed to other components.
    }
}
```

> View the full example code at: [RegistrationExample](../src/Example/InterfaceExamples/RegistrationExample.cs)

### Arena-specific Component Interfaces
As mentioned previously, an `Arena` is a `ComponentBroker`. Interfaces can be registered on an `Arena` to customize behavior for that arena. When `GetInterface` is called on an `Arena`, the `Arena` will try to find the interface locally, but if not found fall back to its parent, the root `ComponentBroker` to find it. This means there can be a "default" implementation registered on the root `ComponentBroker`, and `Arena`-specific implementations to override the default implementation.


## Component Callbacks
Component Callbacks are an implementation of the publisher-subscriber pattern where any component can be a publisher, and any component can be a subscriber. There can be multiple publishers and multiple subscribers.

Registering for a callback on an `Arena` means you only want events for that specific arena.
Registering for a callback on the root `ComponentBroker` means you want all events, including those fired for an arena.

Under the hood, a Component Callback is just a delegate that the `ComponentBroker` is maintaining. When a callback is registered, the `ComponentBroker` just stores the provided delegate and if there already was one, it just combines the provided delegate with the current one. When a callback is unregistered, the `ComponentBroker` just does the opposite. When a callback is to be fired, it's just a matter of getting the current delegate from the `ComponentBroker` and invoking it.

> **Design note:** If you're familiar with other more robust Publisher-subscriber implementations, you might be wondering why doesn't it use weak references. The answer is, I just went for simplicity and speed.

Each of the built-in Component Callbacks use a static helper class to assist with registering, unregistering, and firing. You can use the `ComponentBroker` directly, but the helper class makes it easier to use, especially for firing/invoking a callback. These helpers can be found in the [SS.Core.ComponentCallbacks](../src/Core/ComponentCallbacks) namespace.


### Registering/Unregistering for a Component Callback

> If you register for a callback, make sure you unregister it at some point too. You don't want to have a leak. So, if you register for a callback in your module's `Load` method, remember to unregister it in the `Unload` method. 

Here's an example of registering and unregistering for the PlayerAction callback. The player action callback is probably one of the most used callbacks. Here, I show how you can use it to tell when a player enters or leaves an arena.
```C#
/// <summary>
/// An example on how to register and unregister a callback on root broker (zone-wide).
/// </summary>
public sealed class RegistrationExample(IChat chat) : IModule
{
    private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));

    public bool Load(IComponentBroker broker)
    {
        // Register on the root broker.
        PlayerActionCallback.Register(broker, Callback_PlayerAction);
        return true;
    }

    public bool Unload(IComponentBroker broker)
    {
        // Unregister on the root broker.
        PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
        return true;
    }

    private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.EnterArena)
        {
            _chat.SendArenaMessage(arena, $"Huzzah! {player.Name} entered the arena!");
        }
        else if (action == PlayerAction.LeaveArena)
        {
            _chat.SendArenaMessage(arena, $"Poof! {player.Name} left!");
        }
    }
}
```

> View the full example code at: [RegistrationExample](../src/Example/CallbackExamples/RegistrationExample.cs)

### Register for an Component Callback on an arena
Registering for a Component Callback on an arena is similar, you just have to use the arena, not the root `ComponentBroker`. Here's an example where the `IArenaAttachableModule` interface (which was discussed [earlier](#iarenaattachablemodule)), is used to only register for the PlayerAction Component Callback on arenas that the module is attached to.

```C#
/// <summary>
/// An example on how to register and unregister a callback on an arena.
/// </summary>
/// <param name="chat"></param>
public sealed class ArenaRegistrationExample(IChat chat) : IModule, IArenaAttachableModule
{
    private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));

    bool IModule.Load(IComponentBroker broker)
    {
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        return true;
    }

    bool IArenaAttachableModule.AttachModule(Arena arena)
    {
        // Register on the arena.
        PlayerActionCallback.Register(arena, Callback_PlayerAction);

        return true;
    }

    bool IArenaAttachableModule.DetachModule(Arena arena)
    {
        // Unregister on the arena.
        PlayerActionCallback.Unregister(arena, Callback_PlayerAction);

        return true;
    }

    private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.EnterArena)
        {
            _chat.SendArenaMessage(arena, $"Huzzah! {player.Name} entered the arena!");
        }
        else if (action == PlayerAction.LeaveArena)
        {
            _chat.SendArenaMessage(arena, $"Poof! {player.Name} left!");
        }
    }
}
```

> View the full example code at: [ArenaRegistrationExample](../src/Example/CallbackExamples/ArenaRegistrationExample.cs)

### Creating a new Component Callback
To create a new Component Callback, the bare minimum needed is to define a delegate. However, as mentioned earlier, it's nicer to wrap it all up in a static helper class and use the provided source generator. To use the source generator add a reference to your plug-in project's .csproj file. It'll look like this:
```XML
<ProjectReference Include="..\SourceGeneration\SourceGeneration.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```
> Note: Your path may differ based on where your plug-in project is in relation to the source generator project. The above is from the Example project which is on the same directory level as the SourceGeneration project.

Here's an example of using the source generator to create the static helper class:

```C#
using SS.Core;

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
```

### Firing a Component Callback
When you want to fire (invoke) a Component Callback just use the helper class `Fire` method and pass in the `ComponentBroker` that you want it invoked on. Here's an example firing the Component Callback we just created above:

```C#
// For example, if you had the root broker in a variable named 'broker', 
// you could fire a zone-wide Component Callback by doing:
MyExampleCallback.Fire(broker, 123, "Hello entire zone!", true);

// Or, let's say you had an Arena varible named 'arena'
// you could fire it on that arena with:
MyExampleCallback.Fire(arena, 123, "Hello single arena!", true);
```

> View the full example code at: [CustomExample](../src/Example/CallbackExamples/CustomExample.cs)

Of course, you are not limited to only firing callbacks you've created. You can also fire any of the built-in callbacks if it makes sense for your use case. However, most likely, you'll be firing your own callbacks.

## Component Advisors
Advisors are interfaces that are expected to have more than one implementation. The `ComponentBroker` just keeps track of a collection of instances for each advisor interface type.
- Registering adds an instance to the collection. 
- Unregistering removes an instance from the collection. 
- Using an advisor just means getting a collection of implementations for a specified advsior interface type, and then asking each implementation in the collection for advice on how to proceed with a given task.

### Defining a Component Advisor interface
``` C#
using SS.Core;

public interface IMyExampleAdvisor : IComponentAdvisor
{
    // It can contain any members you want.

    // Here's method that could be used to check 
    // if a player is allowed to do something. 
    // So it has a parameter to pass in the player, 
    // and a bool return value to indicate if the player
    // is allowed.
    //
    // Also, notice that it's possible to include a default
    // implementation. Here, our default implementation
    // returns true by default.
    // 
    // Default implementations are useful if you have many 
    // members and don't want to require implementers to
    // define every member.
    bool IsAllowedToDoSomething(Player player) => false;

    // Here's another example, for a method which 
    // could be used to ask advisors to rate a player.
    // The options are endless, only limited to your imagination.
    int GetRating(Player player) => 10;
}
```

> View the full example code at: [IMyExampleAdvisor](../src/Example/AdvisorExamples/IMyExampleAdvisor.cs)

### Registering and Unregistering an instance of Component Advisor
```C#
/// <summary>
/// This is an example on how to register and unregister a custom advisor.
/// </summary>
public sealed class RegistrationExample : IModule, IMyExampleAdvisor
{
    private AdvisorRegistrationToken<IMyExampleAdvisor>? _iMyExampleAdvisorRegistrationToken;

    bool IModule.Load(IComponentBroker broker)
    {
        // Register the module as an implementer
        // of the IMyExampleAdvisor component advisor interface.
        _iMyExampleAdvisorRegistrationToken = broker.RegisterAdvisor<IMyExampleAdvisor>(this);

        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        // Unregister
        broker.UnregisterAdvisor(ref _iMyExampleAdvisorRegistrationToken);

        return true;
    }

    // Notice that this is explicitly implemented, 
    // that's because we defined a default implementation
    // and this is going to override the default.
    bool IMyExampleAdvisor.IsAllowedToDoSomething(Player player)
    {
        // Whatever logic you decide.
        // This has the advisor say it's allowed if the player
        // is currently in a Warbird.
        return player.Ship == ShipType.Warbird;
    }
}
```

> View the full example code at: [RegistrationExample](../src/Example/AdvisorExamples/RegistrationExample.cs)

### Using a Component Advisor to get advice.
```C#
/// <summary>
/// An example on how to use an advisor.
/// </summary>
/// <param name="broker">The global (zone-wide) broker.</param>
public sealed class UseAdvisorExample(IComponentBroker broker) : IModule
{
    private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    bool IModule.Load(IComponentBroker broker)
    {
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        return true;
    }

    // Make believe this was used at some point
    // in the operation of your module.
    public void DoSomething(Player player)
    {
        bool allow = true;

        // Get the advisors collection.
        var advisors = _broker.GetAdvisors<IMyExampleAdvisor>();

        // Ask each advisor for advice.
        // How you decide to use advice from an advisor is up to you.
        // Here we'll consider something to be allowed, only if
        // every advisor says it's allowed.
        foreach (var advisor in advisors)
        {
            if (!advisor.IsAllowedToDoSomething(player))
            {
                // One advisor said it's not allowed, so we're done.
                // There's no reason to ask other advisors.
                allow = false;
                break;
            }
        }

        if (allow)
        {
            // Do the 'something' that is player is allowed to do.
        }
        else
        {
            // Otherwise, do something else.
        }
    }
}
```

> View the full example code at: [UseAdvisorExample](../src/Example/AdvisorExamples/UseAdvisorExample.cs)


## The `Player` class and Per-player data
The `Player` class is one of the most used and most important types in the server. References of the `Player` type are passed around all throughout the server. As you could have guessed, each instance represents an actual player. This is usually a client connected to the server, though "fake" ones can exist for other reasons too (e.g. playing a recording, AI bots, etc). The `PlayerData` module manages all the `Player` objects and is accessible through the `IPlayerData` interface.

It is very likely that there will be a need to store data about a player when building a module. A module is free to manage its own data structures. So it is entirely possible for a module to maintain a Dictionary that maps from a `Player` (or PlayerId) to a custom piece of data. However, instead of doing that, the `PlayerData` module provides a **per-player** data mechanism.

On the `IPlayerData` interface, there are two methods `AllocatePlayerData` and `FreePlayerData`:
- The `AllocatePlayerData` method reserves a slot for a given data type, provided through a generic type parameter (must be a class). What reserving a slot means, is that each `Player` object will get an instance of that class. `AllocatePlayerData` returns a key. That key can then be used on a `Player` object by calling the `TryGetExtraData` method to get that player's instance of the data.
- The `FreePlayerData` method is used when the slot is no longer needed.

Normally, `AllocatePlayerData` is used when a module loads and `FreePlayerData` is used when a module unloads.

> **Design:**<br>
In ASSS, per-player data is implemented using a chunk of reserved bytes inside of each Player struct. Having the data within the Player struct means that accessing it could be faster, as it's right next to all of the other data for that player (likely read on the same memory page). The chunk of reserved memory is limited in size though. To store a large piece of data, a module would just store a pointer in the per-player data, pointing to some other memory location (allocated with malloc).<br><br>
In Subspace Server .NET, per-player data is stored using a Dictionary in each `Player` object which just contains references to other objects. This is equivalent to how modules in ASSS store pointers in per-player data. If there is a penality for the extra indirection, it is mostly irrelevant. Note: Adding functionality to reserve bytes inside of a `Player`, like ASSS does, is possible using a fixed-sized buffer, but it seems unnecessary.

## The `Arena` class and Per-arena data

The `Arena` class is another type widely used within the server. As you've already learned, it's important as a `ComponentBroker`. It is passed in many callbacks and through many methods on interfaces. As with `Player`, it is very likely there will be a need to store data for an `Arena`. Therefore, the `ArenaManager` module provides a similar mechanism: **per-arena** data. It works identically to per-player data, except for arenas. Use the `IArenaManager` interface to call the `AllocateArenaData` and `FreeArenaData` methods. Use the `TryGetExtraData` method on an `Arena` to access that arena's data.

## IDisposable pattern
- If a module implements the `IDisposable` or `IAsyncDisposable` interface, the server will dispose the module after it is unloaded.
- If Per-Player Data or Per-Arena Data class implements the `IDisposable` interface, the server will handle disposing the object when if it is dropped from use.

## Threading
The server is a multithreaded application. However, for the most part, your module's logic will be running on the mainloop thread. The mainloop thread is the thread that the majority of the server's logic runs on. 

> The actual loop logic of the mainloop thread is in the Mainloop module.

The module methods (`Load`, `Unload`, `PostLoad`, `PreUnload`, `AttachModule`, `DetachModule`) are called on the mainloop thread. Also, the built-in component callbacks are fired on the mainloop thread.

> When firing your own callbacks, it is recommended to invoke them on the mainloop thread as well for consistency. 

It is crucial that any logic processed on the mainloop thread not block execution. If your module needs to perform any blocking I/O (file access, database access, call a REST service, etc.), it needs to be done on a worker thread. The `IMainloop.QueueThreadPoolWorkItem` method provides a wrapper to `ThreadPool.QueueUserWorkItem`, which can be used to transition execution to a worker thread.

> `IMainloop.IsMainloop` can be used to check whether you're on the mainloop thread.

When on a worker thread use `IMainloop.QueueMainWorkItem` to transition execution back to the mainloop thread. You should switch back to the mainloop thread if your logic needs to access the interface of another component, as most modules expect to only be called on the mainloop thread. However, there are exceptions. The following are safe to call from worker threads: 
- `ILogManager` Log methods
- `IChat` Send methods
- `INetwork` Send methods
- TODO: Add better documentation on thread-safe interface methods.

### async/await
The Task asynchronous programming model with `async` / `await` can be used as well. The mainloop thread has a custom `SynchronizationContext` which allows execution to resume on the mainloop thread after an `await`. As normal, you can use [ConfigureAwait](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.configureawait) to control it.

> You might notice that internally, the server itself does not make much use of async/await. This is because: much of the server's logic was based directly off of ASSS, much the server was made before async/await existed, and it aims for zero allocations (see [Performance, allocations, and garbage collection](#performance-allocations-and-garbage-collection)) . As such, when the server needs to do work asynchronously, it mostly uses the callback pattern. That is, a method that does work asynchronously takes in a delegate as a parameter, such that the delegate will be executed on the mainloop thread when the operation is complete.

Be careful of shared data (e.g. Player or Arena objects) when using async/await. If your method is executing on the mainloop thread (the usual case) and you have a reference to a Player object, after an `await`, the Player object may no longer be in the same state if the `await` completed asynchronously. For example, the Player could disconnect by the time the code after the await is executed.

DO feel free to use async/await. The [TeamVersusMatch module](../src/Matchmaking/Modules/TeamVersusMatch.cs) is example that uses it.

### Synchronization
When data is shared between multiple threads, standard synchronization techniques must be used, such as locking. `Player` and `Arena` objects are the most common types of shared data used thoughout the server. Multiple threads read and write to these objects. The `IPlayerData` interface and `IArenaManager` interface each expose collections of `Player` and `Arena` objects respectively. For synchronization, these interfaces provide Lock and Unlock methods that need to be called while accessing the collections.

> The server uses the Lock and Unlock methods of the `IPlayerData` and `IArenaManager` interfaces to synchronize not just access to the collections, but also that of certain data within the `Player` and `Arena` objects. For example, `Player.Status`.

The server executes a lot of logic on the mainloop thread and use the mainloop work queue as a form of synchronization. The mainloop acts as the defining order of the game state. It simply executes queued work items and timers in their proper order. The order that they are processed in is the order that things are considered to have occured. The major state changes to `Player` and `Arena` objects are executed in mainloop timers. In other words, the major `Player` and `Arena` state changes are executed on the mainloop thread. That means, if your code also executes on the mainloop thread, it's *mostly* safe to assume that that no critical changes are being made to the state of Player or Arena objects that you have a reference to.

## Performance, allocations, and garbage collection
It is very easy to allocate memory on the heap in .NET. However, those allocations come at a cost. When the references to that memory go out of scope, it's up to the garbage collector to clean up. Garbage collection takes time. In a real-time video game, having a delay can be very bad. Yes, this is the server-side, but any delay is going to seen as lag. Therefore, memory allocations and garbage collection are still a concern to be aware of.

## String allocations
It is highly recommended that when writing code for the server, that attention be made to not allocate strings when possible. A lot of effort has been put into reducing allocations of string objects. The few remaining places that allocate string objects are: player names, squad names, and arena names. Though, they are pooled to reduce the need to allocate. This was a design decision to make it easier to use those names, rather than have to deal with passing around mutable buffers of characters.

The server extensively uses `Span<char>` and `ReadOnlySpan<char>`, many times in conjuction with `stackalloc`. For example, even when a chat packet is received, no string objects are allocated to process it.

Additionally, `IChat` and `ILogManager` provide method overloads with interpolated string handlers. Underneath the scenes, the interpolated string handlers use pooled `StringBuilder` objects. Therefore, when you call those methods with an interpolated string, there are no string allocations.

When accessing a string keyed `HashSet<T>` or `Dictionary<TKey, TValue>`, avoid allocating a string to by using the `GetAlternateLookup` mechanism added in .NET 9.

> Prior to .NET 9, to get around string allocations for `HashSet` and `Dictionary` lookups, the `SS.Utilties.Trie` and `SS.Utilties.Trie<TValue>` classes provided a workaround. As their name implies, they use an implementation of the trie data structure. The downside was that they used a lot of memory upfront. The `GetAlternateLookup` mechanism has mostly replaced their use. However, they are still useful when you need a collection that can quickly search for keys that start with a given substring.

## Object Pooling
Object pooling is a technique in which objects can be reused. The basic idea behind it is that a pool of objects is maintained. This pool is used such that when an object is needed, it will try to get one from the pool, rather than allocate a new one. And when an object is no longer needed, it can be returned to the pool so that it can be reused later on.

> **Fun fact:** ASSS does pooling too, even though it is in C which doesn't have garbage collection. The ASSS 'net' module keeps a pool of data buffers, so that it doesn't need to allocate memory every time it needs to send or receive data.

### Microsoft.Extensions.ObjectPool
The Microsoft.Extensions.ObjectPool NuGet package provides a very useful implementation of object pooling. It is used extensively in the server. 

The `SS.Utilities.ObjectPool` namespace adds some pooled object policies for commonly used types: `Dictionary<TKey,TValue>`, `HashSet<T>`, `LinkedListNode<T>`, and `List<T>`.

### Objects aware of their pool
In certain scenarios, it makes sense that an object itself keeps track of the pool it originated from, and have the ability to return itself to that pool. For this type of scenario, the `SS.Utilities` assembly contains the `PooledObject` class and associated `Pool` class. `PooledObject` implements the `IDisposable` interface. When disposed, the object returns itself to its originating pool.

> **Design:** If you've ever used ADO.NET, this design is similar to how disposing a database connection returns the connection to a pool to be reused.

### ObjectPoolManager

Rather than having to create your own pools, the `IObjectPoolManager` interface of the `ObjectPoolManager` module provides pools for certain types that may be useful:
- StringBuilderPool: A pool of `StringBuilder` objects.
- PlayerSetPool: A pool of `HashSet<Player>` objects. Useful whenever you need to keep track of a set of `Player` objects.
- ArenaSetPool: A pool of `HashSet<Arena>` objects. Useful when you need to keep track of a set of `Arena` objects.
- NameHashSetPool: A pool of `HashSet<string>` objects that are case-insensitive. Useful if you need to store player names or arena names.

### Object Pooling of Per-Player Data and Per-Arena Data
The per-player data and per-arena data APIs support object pooling too. This can be done in two ways: by implementing the `Microsoft.Extensions.ObjectPool.IResettable` interface OR by using the `IPooledObjectPolicy<T>` overloads of the `IPlayerData.AllocatePlayerData` and `IArenaData.AllocateArenaData` methods.

#### `IResettable`
If the class being used for per-player data or per-arena data implements the `Microsoft.Extensions.ObjectPool.IResettable` interface, the server is able to use a pool for those objects. The `IResettable` interface just contains a `TryReset` method, which is meant to reset the object back to its original state as if it had just been constructed. The idea being, if you're able to reset an object, it can be reused. This approach means the type is aware that it may be used in a pool and is providing the `Reset` functionality itself.

#### `IPooledObjectPolicy<T>` method overloads of `AllocatePlayerData` and `AllocateArenaData`
There are overloads of the `IPlayerData.AllocatePlayerData` and `IArenaData.AllocateArenaData` methods which allow passing in an `IPooledObjectPolicy<T>`. This interface is the one from Microsoft.Extensions.ObjectPool. With a custom policy, you are able to define how an object is created and what to do when an object is being returned to the pool (such as resetting an object's state so that it can be reused).

## Best Practices

Here are some recommendations when writing code for Subspace Server .NET. I've tried to follow these as much as possible, though of course there are always exceptions.

- Don't execute blocking logic on the mainloop thread.
  + If during a module life-cycle step, implement the asynchronous interface (`IAsyncModule`, `IAsyncModuleLoaderAware`, and `IAsyncArenaAttachableModule`) with `async`/`await`.
  + Otherwise, schedule the work to be done on a thread-pool thread or other worker thread.
    - `IMainloop.QueueThreadPoolWorkItem`
    - `Task/Task<T>.Run`
- Avoid throwing exceptions. Only throw exceptions on input validation, which should be noticed during development and not during normal use.
- Reuse objects using [Object Pooling](#object-pooling)
- Reuse array objects using `ArrayPool`.
- Try to avoid allocating strings
  + Use a pooled `StringBuilder` from `IObjectPoolManager.StringBuilderPool`
  + Consider using `Span<char>` when possible.
  + Use the `GetAlternateLookup` mechanism when accessing a string keyed `HashSet`, `Dictionary`, etc...
  + When writing a chat or log message, use interpolated strings, so that it uses the interpolated string handler overloads.
- Try to avoid using LINQ, especially in hot spots (memory allocations).
- Use nullable reference types (why not?).
