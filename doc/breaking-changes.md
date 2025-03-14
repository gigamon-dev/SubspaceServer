# Breaking Changes in Subspace Server .NET releases

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
