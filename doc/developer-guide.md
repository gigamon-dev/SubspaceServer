# Subspace Server .NET - Developer Guide

The modular design that Subspace Server .NET uses mirrors that of ASSS and as such is meant to be completely customizable and extendable. Yes, you can always modify the core server itself by cloning the repository and making your own changes. However, more likely you will want to add your own custom functionality by writing modules that plug-in to the server. This document is to provide some guidance on how to do just that.

> For those familiar with ASSS, writing a module for Subspace Server .NET should be a walk in the park. All of the same concepts apply, the only difference is that you'll be using features built into the C# language rather than all the macro magic ASSS uses.

## Modules

The basic building block of the server is module. The server itself consists of many modules that are working together in unison. A module is simply just a class that the server creates an instance of and calls methods on to `Load` and `Unload`.

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
<module type="TurfReward.TurfModule" path="bin/modules/TurfReward/TurfReward.dll" />
```
Notice that it doesn't include the assembly name in the *type* attribute. Rather, it has a *path* attribute which contains the path of the assembly to load.  Also notice the path is within "bin/modules". That is where you put your own plug-in assemblies containing your custom modules.

> **Fun fact:** You can get a list of loaded modules in-game using the `?lsmod` command.

### How to create a module

First, add a reference the `SS.Core` asssembly.

To create a module, simply create a class and have it implement the `SS.Core.IModule` interface. The `IModule` interface has one method, `Unload`. Create an identical method named `Load`. Here's what it should look like:

```C#
using SS.Core;

public class ExampleModule : IModule
{
    public bool Load(ComponentBroker broker)
    {
        return true;
    }

    public bool Unload(ComponentBroker broker)
    {
        return true;
    }
}
```

Both the `Load` and `Unload` methods return a `bool`, which indicates success (`true`) or failure (`false`). Also, both have a parameter of type `ComponentBroker`. More on that later (see the [ComponentBroker](#ComponentBroker) section below).

You also may be wondering, why the `Load` method is not part of the `IModule` interface. This is because the `Load` method can have additional parameters after the `ComponentBroker` which indicate dependencies that are required.  More on this later.

As you may have guessed from the name, `Load` is called when the server wants to load the module. This normally happens during the [startup](#startup) process mentioned earlier. Likewise, the `Unload` method is called when the server wants to unload the module. Unloading normally happens when the server is shutting down or restarting.

To load the `ExampleModule` module on startup, you'd simply edit the Modules.config with:
```xml
<module type="Example.ExampleModule" path="bin/modules/Example/Example.dll" />
```
That is, you want the server to create an instance of the `ExampleModule` class in the  `Example` namespace of your `Example.dll` assembly which is in the "bin/modules/Example" folder.

> **Fun fact:** Modules can also be manually loaded or unloaded using in-game commands <nobr>`?insmod`</nobr> and <nobr>`?rmmod`</nobr> respectively.

## Module life-cycle
The `Load` and `IModule.Unload` methods are the two required methods of a module. In addition to `IModule`, there are interfaces that can be used to further hook into the steps of a module's life-cycle: `IModuleLoaderAware` and `IArenaAttachableModule`.

Here's the order of method calls to a module:
1. `Load`
2. \(*optional*\) `IModuleLoaderAware.PostLoad`
3. \(*optional*\) `IArenaAttachableModule.AttachModule`
4. \(*optional*\) `IArenaAttachableModule.DetachModule`
5. \(*optional*\) `IModuleLoaderAware.PreUnload`
6. `IModule.Unload`

### IModuleLoaderAware
`IModuleLoaderAware` is an interface that allows you to tie into additional steps of loading and unloading. `IModuleLoaderAware.PostLoad` is an additional step, called during startup after all modules listed in the Modules.config have been loaded.  Likewise, `IModuleLoaderAware.PreUnload` is an additional step called during shutdown, which happens before the `Unload` method of any module is called.

### IArenaAttachableModule
`IArenaAttachableModule` is an interface which gives the ability to do tasks specifically for an arena, rather than server-wide. That is, when you only want your module to affect certain arenas. There is an arena.conf setting `Modules:AttachModules` which allows you to specify the modules that should be attached. Here's an example of what that setting might look like:
```ini
; This is in an arena.conf
[ Modules ]
AttachModules = \
	Example.AnAttachableModule \
	Example.AnotherAttachableModule
```
This tells the server to look for the `Example.AnAttachableModule` and `Example.AnotherAttachableModule`, and call the appropriate `IArenaAttachableModule.AttachModule` and `IArenaAttachableModule.DetachModule` methods when the arena is created or destroyed.

> **Fun fact:** Modules can also be manually attached or detached from an arena using in-game commands <nobr>`?attmod`</nobr> and <nobr>`?detmod`</nobr> respectively.

## ComponentBroker
Now that you know how to create a module and get it to load, you'll want your module to talk to other parts of the the server. This is where the `ComponentBroker` comes in. The `ComponentBroker` acts as an intermediary providing services for modules to interact with one another. That is, modules use a `ComponentBroker` to discover parts from other modules and to expose parts of themselves to other modules. A `ComponentBroker` provides three mechanisms for modules to communicate with one another: [Component Interfaces](#component-interfacesinterfaces), [Component Callbacks](#component-callbackscallbacks), and [Component Advisors](#component-advisorsdvisors). 

> **Note:** In this document I'm referring to Interfaces, Callbacks, and Advisors, with "Component" in their names to be less ambiguious and show that they are meant to be used the `ComponentBroker`. Also, "Components" are usually modules, but that doesn't have to be the case. It is completely possible for a module to pass a `ComponentBroker` to another part which is not a module.

A `ComponentBroker` acts like a container. There is one root `ComponentBroker` which represents the global scope. In other words, server-wide / zone-wide. Next, there are arenas. An `Arena` is a `ComponentBroker`, with the root `ComponentBroker` being its parent. In other words, there's a single root, with each `Arena` as a child leaf. It will make more sense later why this tree structure exists. For now, just know that it exists and that it's all about controlling scope.

> In ASSS, the equivalent of the `ComponentBroker` is the module manager, exposed through the `Imodman` interface.

> ***Fun fact:*** In Subspace Server .NET, there also is a `ModuleManager` and it happens to be the 'root' `ComponentBroker`.

Remember, the `Load` and `Unload` methods? Their first parameter is a `ComponentBroker`.  It's the 'root' `ComponentBroker` that gets passed in.

## Component Interfaces
Component Interfaces are exactly what they sound like, they're normal C# interfaces that a component can register on a `ComponentBroker` for others to find and use.

Normally, there is only one implementation of an Component Interface. However, it is possible for multiple modules to each register an instance for the same ComponentInterface. In this case, the last one registered becomes the 'current' implementation, effectively overriding the previous. Authentication modules which implement the `IAuth` Component Interface use this feature to chain authentication logic on top of each other. That is, one authentication module gets the prior implementation before overriding it. Then, when and if it needs to, it can fail over and call the original implementation.

> Also, you don't need to know this but, you might notice it. When registering an interface, there is an optional parameter for which you can specify a name. This provides a way to differente between multiple registered instances. This functionality is only in a very special case, by encryption modules.

### Getting a Component Interface

First and foremost, you'll probably want to access the interfaces of other parts that are built into the server. So you'll need to know which interface you want. You can find the available built-in interfaces in the [SS.Core.IComponentInterfaces](../src/Core/ComponentInterfaces) namespace. Also, for a listing see the [ASSS Equivalents](asss-equivalents.md) document.

Getting the currently registered instance implementing a Component Interface can be done in two ways:
- Inject required Component Interface dependencies into your module's `Load` method.
- Manually get an interface using `ComponentBroker`.

#### **Injection**
The first parameter to a module's `Load` method is always of type `ComponentBroker`. It is through this parameter that the root broker is supplied. A module's `Load` method can have additional parameters after the `ComponentBroker` parameter. It is through these parameters that required dependencies are declared. These dependencies can be of any Component Interface type. Keep in mind, these dependencies are of interfaces that are considered  ***required***. The server will only load a module if it can find **all** of the required dependencies to call the `Load` method. Optional, interface dependencies should use the Manual method.

> **Fun fact:** The Component Interface functionality of the `ComponentBroker` is a form of *service locator*. The injection of Component Interfaces into a module `Load` method is a form of *dependency injection* that is performed by the `ModuleManager`. Together, they provide a form of *Inversion of Control (IoC)*.

Here is an example of using injection:

```C#
using SS.Core;
using SS.Core.ComponentInterfaces;

public class ExampleModule : IModule
{
    private ILogManager _logManager;

    // Here we declare ILogManager as being a required dependency.
    public bool Load(ComponentBroker broker, ILogManager logManager)
    {
        // You can hold onto the reference and use it until Unload is called.
        // Technically, it's not going to be null.
        // But I like to do the check just in case it gets used
        // by something other than the server, like a unit test.
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

        // Use it.
        _logManager.LogM(LogLevel.Info, nameof(ExampleModule), "Subspace Server .NET is awesome!");

        return true;
    }

    public bool Unload(ComponentBroker broker)
    {
        // For the injected component interfaces, 
        // getting the interface is done for you, and
        // releasing it is too. There's nothing to do.

        // However, we can get rid of the reference.
        // Optional, for the pedantic.
        _logManager = null;

        return true;
    }
}
```

#### **Manually**
Component Interfaces can be manually gotten using the `GetInterface` method of `ComponentBroker`. It will return a reference to the currently registered instance, or null if not found. Getting an Component Interface manually is done when it's an optional dependency. That is, your module can still work without it.

>**IMPORTANT**:  The `ComponentBroker` keeps track of a reference count for Component Interfaces. `GetInterface` increments the count and `ReleaseInterface` decrements the count. Each call to `GetInterface` should have a corresponding `ReleaseInterface`. Failure to do so will prevent modules from unloading because they'll think still being used.

Here's an example of manually getting a Component Interface:

```C#
using SS.Core;
using SS.Core.ComponentInterfaces;

// This example shows a manually gotten Component Interface
// that is used for the entire life of the module.
public class ExampleModule : IModule
{
    private ILogManager _logManager;

    public bool Load(ComponentBroker broker)
    {
        // You can hold onto the reference,  
        // but you must release it at some point.
        _logManager = broker.GetInterface<ILogManager>();

        // Use it, only if it was available.
        // Keep in mind, GetInterface could have returned null.
        // Therefore, checking for null with the ?. (null-conditional operator)
        _logManager?.LogM(LogLevel.Info, nameof(ExampleModule), "Subspace Server .NET is awesome!");

        return true;
    }

    public bool Unload(ComponentBroker broker)
    {
        // Manually gotten Component Interfaces must be manually released.
        // This is necessary!
        // If we had forgotten to do it, the LogManager module would not Unload.
        if (_logManager != null)
            broker.ReleaseInterface(ref _logManager);

        return true;
    }
}
```

Here's another variation of manually getting an interface and releasing it, but this one does not hold onto the interface.
```C#
using SS.Core;
using SS.Core.ComponentInterfaces;

// This is an example that shows getting a Component Interface
// for a short period, using it, and releasing it when done.
public class ExampleModule : IModule
{
    private ComponentBroker _broker;

    public bool Load(ComponentBroker broker)
    {
        // You can hold a reference to the root broker for later use.
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        
        return true;
    }

    public bool Unload(ComponentBroker broker)
    {
        return true;
    }

    // Make believe something calls this method.
    public void DoSomething()
    {
        // Try to get it.
        ILogManager logManager = _broker.GetInterface<ILogManager>();

        // Check whether it was available.
        if (logManager != null)
        {
            // It was available.
            try
            {
                // Use it.
                logManager.LogM(LogLevel.Info, nameof(ExampleModule), 
                    "Subspace Server .NET is awesome!");
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
using SS.Core;

public class ExampleModule : IModule, IMyExample
{
    private InterfaceRegistrationToken<IMyExample> _iMyExampleRegistrationToken;

    public bool Load(ComponentBroker broker)
    {
        // Initialize and get ready.

        // Register the interface.
        // This is normally done at the end of Load, when everything is initialized and ready.
        // Notice the return value is a token that we'll later use to unregister.
        _iMyExampleRegistrationToken = broker.RegisterInterface<IMyExample>(this);
        return true;
    }

    public bool Unload(ComponentBroker broker)
    {
        // Unregister the interface.
        // This is normally the first thing done in Unload.
        if (broker.UnregisterInterface(ref _iMyExampleRegistrationToken) != 0)
            return false;

        // Do other cleanup now that others should no longer be accessing us.

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
using SS.Core;
using SS.Core.ComponentCallbacks;

public class ExampleModule : IModule
{
    public bool Load(ComponentBroker broker)
    {
        // Register on the root broker.
        PlayerActionCallback.Register(broker, Callback_PlayerAction);

        return true;
    }

    public bool Unload(ComponentBroker broker)
    {
        // Unregister on the root broker.
        PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

        return true;
    }

    private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
    {
        if (action == PlayerAction.EnterArena)
        {
            // Do whatever is needed for a player entering an arena.
        }
        else if (action == PlayerAction.LeaveArena)
        {
            // Do whatever is needed for a player leaving an arena.
        }
    }
}
```

### Register for an Component Callback on an arena
Registering for a Component Callback on an arena is similar, you just have to use the arena, not the root `ComponentBroker`. Here's an example where the `IArenaAttachableModule` interface (which was discussed [earlier](#iarenaattachablemodule)), is used to only register for the PlayerAction Component Callback on arenas that the module is attached to.

```C#
using SS.Core;
using SS.Core.ComponentCallbacks;

public class ExampleModule : IModule, IArenaAttachableModule
{
    public bool Load(ComponentBroker broker)
    {
        return true;
    }

    public bool Unload(ComponentBroker broker)
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

    private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
    {
        if (action == PlayerAction.EnterArena)
        {
            // Do whatever is needed for a player entering an arena.
        }
        else if (action == PlayerAction.LeaveArena)
        {
            // Do whatever is needed for a player leaving an arena.
        }
    }
}
```

### Creating a new Component Callback
To create a new Component Callback, the bare minimum needed is to define a delegate. However, as mentioned earlier, it's nicer to wrap it all up in a static helper class. Here's an example of how to do that.

> On the helper class, there's no requirement that the names of the methods be `Register`, `Unregister`, and `Fire`. However, it is easy to understand and so it's recommended to just stick with those names as a convention.

```C#
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
    public static void Register(ComponentBroker broker, MyExampleDelegate handler)
    {
        broker?.RegisterCallback(handler);
    }

    // This is the helper method for unregistering.
    // It just wraps the call to the ComponentBroker.
    public static void Unregister(ComponentBroker broker, MyExampleDelegate handler)
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
    public static void Fire(ComponentBroker broker, int foo, string bar, bool baz)
    {
        // Invoke it on the broker.
        broker?.GetCallback<MyExampleDelegate>()?.Invoke(foo, bar, baz);

        // Recursively fire it on the parent of the broker (if there is a parent).
        if (broker?.Parent != null)
            Fire(broker.Parent, foo, bar, baz);
    }
}
```

### Firing a Component Callback
When you want to fire (invoke) a Component Callback just use the helper class `Fire` method and pass in the `ComponentBroker` that you want it invoked on. Here's an example firing the Component Callback we just created above:

```C#
// For example, if you had the root broker in a variable named 'broker', 
// you could fire a zone-wide Component Callback by doing:
MyExampleCallback.Fire(broker, 123, "Subspace forever!", true);

// Or, let's say you had an Arena varible named 'arena'
// you could fire it on that arena with:
MyExampleCallback.Fire(arena, 123, "Subspace forever!", true);

```

Of course, you are not limited to only firing callbacks you defined. You can also fire any of the built-in callbacks if it makes sense for your use case. However, most likely, you'll be firing callbacks that you defined.

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

### Registering and Unregistering an instance of Component Advisor
```C#
using SS.Core;

public class ExampleAdvisorModule : IModule, IMyExampleAdvisor
{
    private AdvisorRegistrationToken<IMyExampleAdvisor> _iMyExampleAdvisorRegistrationToken;

    public bool Load(ComponentBroker broker)
    {
        // Register the module as an implementer 
        // of the IMyExampleAdvisor component advisor interface.
        _iMyExampleAdvisorRegistrationToken = broker.RegisterAdvisor<IMyExampleAdvisor>(this);

        return true;
    }

    public bool Unload(ComponentBroker broker)
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

### Using a Component Advisor to get advice.
```C#
using SS.Core;

public class UseAdvisorExampleModule : IModule
{
    private ComponentBroker _broker;

    public bool Load(ComponentBroker broker)
    {
        // Hold a reference to the root broker for later use.
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));

        return true;
    }

    public bool Unload(ComponentBroker broker)
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
        foreach (var advisor in advsiors)
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
- If a module implements the `IDisposable` interface, the server will call the `Dispose` method after the module is unloaded.
- If the class provided for Per-Player Data or Per-Arena Data implements the `IDisposable` interface, the server handles calling the `Dispose` method before the object is dropped from use.

## Object Pooling
As you probably already know, is very easy to allocate memory on the heap in .NET. However, those allocations come at a cost. When the references to that memory go out of scope, it's up to the garbage collector to clean up. Garbage collection takes time. In a real-time video game, having a delay can be very bad. Yes, this is the server-side, but any delay is going to seen as lag. Therefore, memory allocations and garbage collection are of a concern.

Object pooling is a technique in which objects can be reused. The basic idea behind it is that a pool of objects is maintained. This pool is used such that when an object is needed, it will try to get one from the pool, rather than allocate a new one. And when an object is no longer needed, it can be returned to the pool so that it can be reused later on.

> **Fun fact:** ASSS does pooling too, even though it is in C which doesn't have garbage collection. The ASSS 'net' module keeps a pool of data buffers, so that it doesn't need to allocate memory every time it needs to send or receive data.

### Microsoft.Extensions.ObjectPool
The Microsoft.Extensions.ObjectPool NuGet package provides a very useful implementation of object pooling. It is used extensively in the server. However, keep in mind, the pools that Microsoft.Extensions.ObjectPool provides is not ideal for every use case. The pooling functionality Microsoft.Extensions.ObjectPool contains is geared toward short-term object use. That is, for scenarios where an object is used for a very short time, and then returned back to the pool. 

Out of the box, the pools Microsoft.Extensions.ObjectPool provides is not a good match for use cases where objects may be held for extended periods of time, such as a producer-consumer queue where hundreds or thousands of objects may be held up waiting to be procesed, and eventually to be released. To cover this scenario, the `SS.Utilities` assembly contains an implementation, `NonTransientObjectPool<T>`. It is a very simple implementation which uses a `System.Collection.Concurrent.ConcurrentBag<T>`. This implementation may not be the best solution possible, but it gets the job done.

### Objects aware of their pool
In certain scenarios, it makes sense that an object itself keeps track of the pool it originated from, and have the ability to return itself to that pool. For this type of scenario, the `SS.Utilities` assembly contains the `PooledObject` class and associated `Pool` class. `PooledObject` implements the `IDisposable` interface. When disposed, the object returns itself to its originating pool.

> **Design:** If you've ever used ADO.NET, this design is similar to how disposing a database connection returns the connection to a pool to be reused.

### ObjectPoolManager

Rather than having to create your own pools, the `IObjectPoolManager` interface of the `ObjectPoolManager` module provides access to pools for certain types that may be useful.
- StringBuilderPool: A pool of StringBuilder objects.
- PlayerSetPool: A pool of HashSet<Player> objects. Useful whenever you need to keep track of a set of `Player` objects.

### Object Pooling of Per-Player Data and Per-Arena Data
The per-player data and per-arena data APIs support object pooling too. This can be done in two ways: by implementing the `IPooledExtraData` interface OR by using special overloads of the `IPlayerData.AllocatePlayerData` and `IArenaData.AllocateArenaData` methods. Each way has its own pros and cons.

#### `IPooledExtraData`
If the class being used for per-player data or per-arena data implements the `SS.Core.IPooledExtraData` interface, the server is able to use a pool for those objects. The `IPooledExtraData` interface just contains a `Reset` method, which is meant to reset the object back to its original state as if it had just been constructed. The idea being, if you're able to reset an object, it can be reused. This approach means the type is aware that it may be used in a pool and is providing the `Reset` functionality itself.

#### `IPooledObjectPolicy\<T\>` method overloads of `AllocatePlayerData` and `AllocateArenaData`
There are overloads of the `IPlayerData.AllocatePlayerData` and `IArenaData.AllocateArenaData` methods which allow passing in an `IPooledObjectPolicy\<T\>`. This interface is the one from Microsoft.Extensions.ObjectPool. With a custom policy, you are able to define how an object is created and what to do when an object is being returned to the pool (such as resetting an object's state so that it's ok to be reused).