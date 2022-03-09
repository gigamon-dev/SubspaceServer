using System;

namespace SS.Core.ComponentInterfaces
{
    public readonly struct LvzToggle
    {
        public readonly short Id;
        public readonly bool IsEnabled;

        public LvzToggle(short id, bool isEnabled)
        {
            if (id < 0)
                throw new ArgumentOutOfRangeException(nameof(id), "Cannot be negative.");

            Id = id;
            IsEnabled = isEnabled;
        }
    }

    /// <summary>
    /// Interface for a service that controls lvz objects.
    /// </summary>
    public interface ILvzObjects : IComponentInterface
    {
        void SendState(Player player);

        /// <summary>
        /// Toggles a lvz object.
        /// </summary>
        /// <remarks>
        /// If you know you're going to toggle multiple objects, it's more efficient if you call <see cref="ToggleSet(ITarget, Span{LvzToggle})"/>.
        /// </remarks>
        /// <param name="target">The target representing which player(s) to send the change to.</param>
        /// <param name="id">The Id of the lvz object to toggle.</param>
        /// <param name="isEnabled"><see langword="true"/> to enable the object. <see langword="false"/> to disable the object.</param>
        void Toggle(ITarget target, short id, bool isEnabled);

        /// <summary>
        /// Toggles a set of lvz objects.
        /// </summary>
        /// <remarks>
        /// The lvz object changes are sent togther.
        /// </remarks>
        /// <param name="target">The target representing which player(s) to send the change to.</param>
        /// <param name="set">The collection of changes to make.</param>
        void ToggleSet(ITarget target, ReadOnlySpan<LvzToggle> set);

        void Move(ITarget target, int id, int x, int y, int rx, int ry);

        void Image(ITarget target, int id, int image);

        void Layer(ITarget target, int id, int layer);

        void Timer(ITarget target, int id, int time);

        void Mode(ITarget target, int id, int mode);

        void Reset(Arena arena, int id);

        //InfoDefault

        //InfoCurrent
    }
}
