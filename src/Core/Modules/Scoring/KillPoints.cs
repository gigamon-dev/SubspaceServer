using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;

namespace SS.Core.Modules.Scoring
{
    /// <summary>
    /// Module that advises the <see cref="Game"/> module on how many points to reward when a player is killed.
    /// </summary>
    /// <remarks>
    /// The reward amount is based on the following arena settings:
    /// <list type="bullet">
    ///     <item>Kill:FixedKillReward</item>
    ///     <item>Kill:FlagMinimumBounty</item>
    ///     <item>Kill:PointsPerKilledFlag</item>
    ///     <item>Kill:PointsPerCarriedFlag</item>
    ///     <item>Kill:PointsPerTeamFlag</item>
    ///     <item>Kill:TeamKillPoints</item>
    /// </list>
    /// </remarks>
    [CoreModuleInfo]
    public class KillPoints : IModule, IArenaAttachableModule, IKillAdvisor
    {
        private readonly IArenaManager _arenaManager;
        private readonly IConfigManager _configManager;

        private ArenaDataKey<ArenaData> _adKey;

        public KillPoints(
            IArenaManager arenaManager,
            IConfigManager configManager)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();

            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        public bool AttachModule(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            ad.KillAdvisorRegistrationToken = arena.RegisterAdvisor<IKillAdvisor>(this);

            return true;
        }

        public bool DetachModule(Arena arena)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return false;

            if (ad.KillAdvisorRegistrationToken != null)
                arena.UnregisterAdvisor(ref ad.KillAdvisorRegistrationToken);

            return true;
        }

        #endregion

        #region IKillAdvisor

        short IKillAdvisor.KillPoints(Arena arena, Player killer, Player killed, int bounty, int flags)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return 0;

            if (killer.Freq == killed.Freq // is a teamkill
                && !ad.TeamKillPoints) // and not awarding points for teamkills
            {
                return 0;
            }

            int points = ad.FixedKillReward != -1 ? ad.FixedKillReward : bounty;

            if (killer.Position.Bounty >= ad.FlagMinimumBounty)
            {
                if (flags > 0)
                {
                    points += flags * ad.PointsPerKilledFlag;
                }

                if (killer.Packet.FlagsCarried > 0)
                {
                    points += killer.Packet.FlagsCarried * ad.PointsPerCarriedFlag;
                }

                IFlagGame? flagGame = arena.GetInterface<IFlagGame>();
                if (flagGame != null)
                {
                    try
                    {
                        int freqFlags = flagGame.GetFlagCount(arena, killer.Freq);
                        if (freqFlags > 0)
                        {
                            points += freqFlags * ad.PointsPerTeamFlag;
                        }
                    }
                    finally
                    {
                        arena.ReleaseInterface(ref flagGame);
                    }
                }
            }

            return (short)points;
        }

        #endregion

        #region Callbacks

        [ConfigHelp("Kill", "FixedKillReward", ConfigScope.Arena, typeof(int), DefaultValue = "-1",
            Description = "If -1 use the bounty of the killed player to calculate kill reward. Otherwise use this fixed value.")]
        [ConfigHelp("Kill", "FlagMinimumBounty", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "The minimum bounty the killing player must have to get any bonus kill points for flags transferred, carried or owned.")]
        [ConfigHelp("Kill", "PointsPerKilledFlag", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = """
                The number of extra points to give for each flag a killed player
                was carrying.Note that the flags don't actually have to be
                transferred to the killer to be counted here.
                """)]
        [ConfigHelp("Kill", "PointsPerCarriedFlag", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = """
                The number of extra points to give for each flag the killing
                player is carrying.Note that flags that were transfered to
                the killer as part of the kill are counted here, so adjust
                PointsPerKilledFlag accordingly.
                """)]
        [ConfigHelp("Kill", "PointsPerTeamFlag", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = """
                The number of extra points to give for each flag owned by
                the killing team.Note that flags that were transfered to
                the killer as part of the kill are counted here, so
                adjust PointsPerKilledFlag accordingly.
                """)]
        [ConfigHelp("Misc", "TeamKillPoints", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
            Description = "Whether points are awarded for a team-kill.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ConfigHandle ch = arena.Cfg!;
                ad.FixedKillReward = _configManager.GetInt(ch, "Kill", "FixedKillReward", -1);
                ad.FlagMinimumBounty = _configManager.GetInt(ch, "Kill", "FlagMinimumBounty", 0);
                ad.PointsPerKilledFlag = _configManager.GetInt(ch, "Kill", "PointsPerKilledFlag", 0);
                ad.PointsPerCarriedFlag = _configManager.GetInt(ch, "Kill", "PointsPerCarriedFlag", 0);
                ad.PointsPerTeamFlag = _configManager.GetInt(ch, "Kill", "PointsPerTeamFlag", 0);
                ad.TeamKillPoints = _configManager.GetInt(ch, "Misc", "TeamKillPoints", 0) != 0;
            }
        }

        #endregion

        #region Helper types

        private class ArenaData : IResettable
        {
            // settings
            public int FixedKillReward;
            public int FlagMinimumBounty;
            public int PointsPerKilledFlag;
            public int PointsPerCarriedFlag;
            public int PointsPerTeamFlag;
            public bool TeamKillPoints;

            public AdvisorRegistrationToken<IKillAdvisor>? KillAdvisorRegistrationToken;

            public bool TryReset()
            {
                FixedKillReward = 0;
                FlagMinimumBounty = 0;
                PointsPerKilledFlag = 0;
                PointsPerCarriedFlag = 0;
                PointsPerTeamFlag = 0;
                TeamKillPoints = false;
                return true;
            }
        }

        #endregion
    }
}
