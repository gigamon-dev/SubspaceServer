using SS.Core.ComponentInterfaces;

namespace SS.Core.Modules.FlagGame
{
    /// <summary>
    /// Module that manages flag games where flags can be carried.
    /// E.g. jackpot zone, running zone, warzone ctf.
    /// </summary>
    public class CarryFlags : IModule, ICarryFlagGame
    {
        private InterfaceRegistrationToken<IFlagGame> _iFlagGameRegistrationToken;
        private InterfaceRegistrationToken<ICarryFlagGame> _iCarryFlagGameRegistrationToken;

        #region Module members

        public bool Load(ComponentBroker broker)
        {
            _iFlagGameRegistrationToken = broker.RegisterInterface<IFlagGame>(this);
            _iCarryFlagGameRegistrationToken = broker.RegisterInterface<ICarryFlagGame>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iFlagGameRegistrationToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iCarryFlagGameRegistrationToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IFlagGame

        public void ResetGame(Arena arena)
        {
            throw new System.NotImplementedException();
        }

        public int GetFlagCount(Arena arena)
        {
            throw new System.NotImplementedException();
        }

        public int GetFlagCount(Arena arena, int freq)
        {
            throw new System.NotImplementedException();
        }

        public int GetFlagCount(Player player)
        {
            throw new System.NotImplementedException();
        }

        #endregion
    }
}
