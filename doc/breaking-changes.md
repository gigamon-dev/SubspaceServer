# Breaking Changes in Subspace Server .NET releases

## v5.0.0

The source generator for pub/sub callbacks has been simplified using C# 14 extension members. This is a source incompatible change which requires modifying how pub/sub callbacks are declared.

Previously, to use the generator you would wrap the delegate in a static partial helper class, which the source generator would add methods to. The `[CallbackHelper]` attribute was used to mark the class so that the source generator would know to generate for it. The wrapper class name had to end with "Callback" and delegate name would need to match, with the name ending with "Delegate". For example,
```C#
[CallbackHelper]
public static partial class ArenaActionCallback
{
    public delegate void ArenaActionDelegate(Arena arena, ArenaAction action);
}
```

As of v5.0.0, the static partial helper class is no longer needed and there are no longer any naming requirements. You just define a delegate and mark it with the `[ComponentCallback]` marker attribute. For example,

```C#
[ComponentCallback]
public delegate void ArenaActionCallback(Arena arena, ArenaAction action);
```

The easiest, recommended, way to migrate existing callbacks is to remove the static partial class and rename the delegate to it. Doing it this way means you will not need to change any of the calling code.

## v4.0.0

- The BillingUdp module was updated to reduce memory allocations. This includes a modification to the `IBilling` interface. Encryption module binaries from earlier releases are no longer compatible.
- The `IPeriodicRewardPoints.GetRewardPoints` was modified to reduce memory allocations. It previously had an IReadOnlyDictionary parameter which would have its enumerator boxed.
- The `PacketHandler` handler delegate was modified to pass the data as `ReadOnlySpan<byte>` instead of `Span<byte>`.

## v3.0.0

- Persist
	+ `IPersist` registration methods were modified to be async.
	+ Additional `IPersistDatastore` methods: `BeginTransaction` and `CommitTransaction`.
- Carry Flag Game
	+ Added method `ICarryFlagGame.GetPlayerKillTransferCount`.
	+ Added method `ICarryFlagBehavior.GetPlayerKillTransferCount`.

## v2.0.0

There are too many breaking changes, and it wouldn't make sense to list them all. However, here are the most important changes to know about when upgrading to v2.0.0:

- Module redesign
	+ Modules now get required dependencies injected into their constructor, rather than in the Load method.
	+ The `IModule` interface now contains the `Load` method. Previously, the `ModuleManager` searched for the best `Load` method using reflection.
	+ Both the `Load` and `Unload` methods pass `IComponentBroker` instead of `ComponentBroker`.
- Network module changes to reduce memory allocations by using `SocketAddress` intead of `IPEndPoint`.
  > The encryption module binaries from v1.0.0 are not compatible. When upgrading, use the provided v2.0.0 binaries.
- `SS.Core.ComponentInterfaces.PacketDelegate` changed to use `Span<byte>` instead of a byte array.
- Per-player data and per-arena data: Removed the `SS.Core.IPooledExtraData` interface. Use `Microsoft.Extensions.ObjectPool.IResettable` instead.
- Sealed all module classes. The modules were never designed to be derived from.
