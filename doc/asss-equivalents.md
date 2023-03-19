# Equivalents to ASSS
This document provides an overview of the various server parts and their relation to those in ASSS. For the most part, there is a one-to-one mapping to the parts within ASSS.

## Modules
| ASSS  | Subspace Server .NET | Notes |
| --- | --- | --- |
| admincmd | SS.Core.Modules.AdminCommand | |
| aliasdb | - | No plans to add it. Use billing server functionality instead. |
| ap_multipub | SS.Core.Modules.ArenaPlaceMultiPub | |
| arenaman | SS.Core.Modules.ArenaManager | |
| arenaperm | | |
| auth_ban | SS.Core.Modules.AuthBan | |
| auth_file | SS.Core.Modules.AuthFile | |
| auth_prefix | | |
| contrib:auth_vie | - | No plans to add it. |
| funky:autoturret | - | No plans to add it. |
| funky:autowarp | SS.Core.Modules.AutoWarp | |
| balls | SS.Core.Modules.Balls | |
| banners | SS.Core.Modules.Banners | |
| scoring:basicstats | SS.Core.Modules.Scoring.BasicStats | |
| billing | - | No plans to add it. Use BillingUdp instead. |
| billing_ssc | SS.Core.Modules.BillingUdp | |
| bricks | SS.Core.Modules.Bricks | TODO: additional brick modes |
| funky:brickwriter | - |  No plans to add it.  |
| buy | SS.Core.Modules.Buy | |
| bw_default | SS.Core.Modules.BandwidthDefault | |
| bw_nolimit | SS.Core.Modules.BandwidthNoLimit | |
| capman | SS.Core.Modules.CapabilityManager | |
| cfghelp | SS.Core.Modules.Help | |
| chat | SS.Core.Modules.Chat | |
| chatnet |  | TODO: very low priority |
| clientset | SS.Core.Modules.ClientSettings | |
| cmdlist | SS.Core.Modules.CommandManager | |
| cmdman | SS.Core.Modules.CommandManager | |
| config | SS.Core.Modules.ConfigManager | |
| core | SS.Core.Modules.Core | |
| deadlock | - | No plans to add it. |
| directory | SS.Core.Modules.DirectoryPublisher | |
| enc_null | SS.Core.Modules.EncryptionNull | |
| enc_vie | SS.Core.Modules.EncryptionVIE | |
| enc_cont:enc_cont | SS.EncryptionContinuum.EncryptionContinuum | Closed source<br>TODO: provide a native binary like ASSS does. |
| enf_flagwin | - | No plans to add it. |
| enf_legalship | SS.Core.Modules.Enforcers.LegalShip | |
| enf_lockspec | SS.Core.Modules.Enforcers.LockSpec | |
| enf_shipchange | SS.Core.Modules.Enforcers.ShipChange | |
| enf_shipcount | - | No plans to add it. |
| fake | SS.Core.Modules.Fake | |
| filetrans | SS.Core.Modules.FileTransfer | |
| flagcore | SS.Core.Modules.FlagGame.CarryFlags<br>SS.Core.Modules.FlagGame.StaticFlags | |
| freqman | SS.Core.Modules.FreqManager | |
| freqowners | - | No plans to add it. |
| game | SS.Core.Modules.Game | |
| game_timer | SS.Core.Modules.GameTimer | |
| help | SS.Core.Modules.Help | |
| idle | - | No plans to add it. |
| scoring:jackpot | SS.Core.Modules.Scoring.Jackpot | |
| koth | SS.Core.Modules.Crowns<br>SS.Core.Modules.Scoring.Koth | |
| lagaction | SS.Core.Modules.LagAction | |
| lagdata | SS.Core.Modules.LagData | |
| log_console | SS.Core.Modules.LogConsole | |
| log_file | SS.Core.Modules.LogFile | |
| logman | SS.Core.Modules.LogManager | |
| log_staff | SS.Core.Modules.CommandWatch | |
| log_sysop | SS.Core.Modules.LogSysop | |
| mainloop | SS.Core.Modules.Mainloop | |
| mapdata | SS.Core.Modules.MapData | |
| mapnewsdl | SS.Core.Modules.MapNewsDownload | |
| contrib:mark | - | No plans to add it. |
| messages | SS.Core.Modules.Messages | |
| mysql | - | No plans to add it. |
| net | SS.Core.Modules.Network | |
| notify | SS.Core.Module.Notify | |
| objects | SS.Core.Modules.LvzObjects | |
| obscene | SS.Core.Modules.Obscene | |
| peer | SS.Core.Modules.Peer | |
| scoring:periodic | SS.Core.Modules.Scoring.PeriodicReward | |
| persist | SS.Core.Modules.Persist | |
| playercmd | SS.Core.Modules.PlayerCommand | |
| playerdata | SS.Core.Modules.PlayerData | |
| scoring:points_flag | SS.Core.Modules.Scoring.FlagGamePoints | |
| scoring:points_goal | SS.Core.Modules.Scoring.BallGamePoints | |
| scoring:points_kill | SS.Core.Modules.Scoring.KillPoints | |
| scoring:points_periodic | SS.Core.Modules.Scoring.PeriodicReward | |
| prng | SS.Core.Modules.Prng | |
| pymod | n/a | |
| quickfix | SS.Core.Modules.Quickfix | |
| funky:record | SS.Replay.ReplayModule | |
| redirect | SS.Core.Modules.Redirect | |
| security | SS.Core.Modules.Security | |
| sendfile | - | No plans to add it. |
| funky:sgcompat | SS.Core.Modules.SubgameCompatibility | Partial, might add more later, low priority |
| - | SS.Core.Modules.Scoring.SpeedGame | |
| stats | SS.Core.Modules.Scoring.Stats | |
| unixsignal | | No plans to add it. |
| contrib:voices | - | No plans to add it. |
| watchdamage | SS.Core.Modules.WatchDamage | |
| &lt;py&gt; exceptlogging | - | No plans to add it. |
| &lt;py&gt; fm_password | - | No plans to add it. |
| &lt;py&gt; exec | - | No plans to add it. |
| &lt;py&gt; fg_turf | SS.Core.Modules.FlagGame.StaticFlags | |
| &lt;py&gt; fg_wz | SS.Core.Modules.FlagGame.CarryFlags<br>SS.Core.Modules.Scoring.FlagGamePoints | |
| &lt;py&gt; watchgreen | - | No plans to add it. |

## Interfaces
| ASSS  | Subspace Server .NET | Notes |
| --- | --- | --- |
| Iarenaman | SS.Core.ComponentInterfaces.IArenaManager | |
| Iarenaplace | SS.Core.ComponentInterfaces.IArenaPlace | |
| Iauth | SS.Core.ComponentInterfaces.IAuth | |
| Ibalancer | SS.Core.ComponentInterfaces.IFreqBalancer | |
| Iballs | SS.Core.ComponentInterfaces.IBalls | |
| - | SS.Core.ComponentInterfaces.IBallGamePoints | |
| Ibanners | SS.Core.ComponentInterfaces.IBanners | |
| Ibilling | SS.Core.ComponentInterfaces.IBilling | |
| Ibillingfallback | SS.Core.ComponentInterfaces.IBillingFallback | |
| Ibricks | SS.Core.ComponentInterfaces.IBrickManager | |
| Ibrickhandler | SS.Core.ComponentInterfaces.IBrickHandler | |
| Ibrickwriter | - | No plans to convert it. |
| Ibwlimit | SS.Core.ComponentInterfaces.IBandwidthLimiterProvider | |
| Icapman | SS.Core.ComponentInterfaces.ICapabilityManager | |
| Icfghelp | SS.Core.ComponentInterfaces.IConfigHelp | |
| Ichat | SS.Core.ComponentInterfaces.IChat | |
| Ichatnet | SS.Core.ComponentInterfaces.IChatNet | TODO: Not implemented. |
| Iclientset | SS.Core.ComponentInterfaces.IClientSettings | |
| Icmdman | SS.Core.ComponentInterfaces.ICommandManager | |
| Iconfig | SS.Core.ComponentInterfaces.IConfigManager | |
| Iencrypt | SS.Core.ComponentInterfaces.IEncrypt | |
| Iclientencrypt | SS.Core.ComponentInterfaces.IClientEncrypt | |
| Ifake | SS.Core.ComponentInterfaces.IFake | |
| Ifiletrans | SS.Core.ComponentInterfaces.IFileTransfer | |
| Iflagcore | SS.Core.ComponentInterfaces.IFlagGame<br>SS.Core.ComponentInterfaces.ICarryFlagGame<br>SS.Core.ComponentInterfaces.IStaticFlagGame | |
| Iflaggame | SS.Core.ComponentInterfaces.ICarryFlagBehavior | |
| Ifreqman | SS.Core.ComponentInterfaces.IFreqManager | |
| Igame | SS.Core.ComponentInterfaces.IGame | |
| - | SS.Core.ComponentInterfaces.IGameTimer | |
| Igroupman | SS.Core.ComponentInterfaces.IGroupManager | |
| Iidle | - | |
| Ijackpot | SS.Core.ComponentInterfaces.IJackpot | |
| Ikillgreen | SS.Core.ComponentInterfaces.IKillGreen | |
| Ilagquery | SS.Core.ComponentInterfaces.ILagQuery | |
| Ilagcollect | SS.Core.ComponentInterfaces.ILagCollect | |
| Ilog_file | SS.Core.ComponentInterfaces.ILogFile | |
| Ilogman | SS.Core.ComponentInterfaces.ILogManager | |
| Imainloop | SS.Core.ComponentInterfaces.IMainloop<br>SS.Core.ComponentInterfaces.IMainloopTimer<br>SS.Core.ComponentInterfaces.IServerTimer | |
| Imapdata | SS.Core.ComponentInterfaces.IMapData | |
| Imapnewsdl | SS.Core.ComponentInterfaces.IMapNewsDownload | |
| Imodman | SS.Core.ComponentInterfaces.IModuleManager | |
| Inet_client | SS.Core.ComponentInterfaces.INetworkClient | |
| Inet | SS.Core.ComponentInterfaces.INetwork<br>SS.Core.ComponentInterfaces.INetworkEncryption | |
| Iobjects | SS.Core.ComponentInterfaces.ILvzObjects | |
| Iobscene | SS.Core.ComponentInterfaces.IObscene | |
| Ioptparser | n/a | |
| Ipeer | SS.Core.ComponentInterfaces.IPeer | |
| - | SS.Core.ComponentInterfaces.IPeriodicReward | |
| Iperiodicpoints | SS.Core.ComponentInterfaces.IPeriodicRewardPoints | |
| Ipoints_koth | n/a | |
| Ipersist | SS.Core.ComponentInterfaces.IPersist<br>SS.Core.ComponentInterfaces.IPersistExecutor | |
| - | SS.Core.ComponentInterfaces.IPersistDatastore | A way to plug in a different database for the Persist module. |
| Iplayerdata | SS.Core.ComponentInterfaces.IPlayerData | |
| - | SS.Core.ComponentInterfaces.IObjectPoolManager | |
| Iprng | SS.Core.ComponentInterfaces.IPrng | |
| Iredirect | SS.Core.ComponentInterfaces.IRedirect | |
| Ireldb | - | No plans to convert it. |
| - | SS.Core.ComponentInterfaces.ISecuritySeedSync | |
| Istats | SS.Core.ComponentInterfaces.IGlobalPlayerStats<br>SS.Core.ComponentInterfaces.IArenaPlayerStats<br>SS.Core.ComponentInterfaces.IAllPlayerStats<br>SS.Core.ComponentInterfaces.IScoreStats | ASSS only tracks per-arena stats. This server tracks both global (zone-wide) stats and per-arena stats. As such, there are separate interfaces for each and one to affect both simultaneously, IAllPlayerStats.  The IArenaPlayerStats interface is the closest match to Istats in ASSS since that affects per-arena stats. |
| Iwatchdamage | SS.Core.ComponentInterfaces.IWatchDamage | |

## Callbacks
| ASSS  | Subspace Server .NET | Notes |
| --- | --- | --- |
| CB_ARENAACTION | SS.Core.ComponentCallbacks.ArenaActionCallback | |
| CB_ATTACH | SS.Core.ComponentCallbacks.AttachCallback | |
| CB_BALLCOUNTCHANGE | SS.Core.ComponentCallbacks.BallCountChangedCallback | |
| CB_BALLFIRE | SS.Core.ComponentCallbacks.BallShootCallback | |
| CB_BALLPICKUP | SS.Core.ComponentCallbacks.BallPickupCallback | |
| - | SS.Core.ComponentCallbacks.BricksPlacedCallback | |
| CB_CHATMSG | SS.Core.ComponentCallbacks.ChatMessageCallback | |
| - | SS.Core.ComponentCallbacks.CommandExecutedCallback | |
| CB_CONNINIT | n/a | Use INetworkEncryption.AppendConnectionInitHandler instead. |
| CB_CROWNCHANGE | SS.Core.ComponentCallbacks.CrownToggledCallback | |
| CB_DOBRICKMODE | SS.Core.ComponentCallbacks.DoBrickModeCallback | |
| CB_FLAGGAIN | SS.Core.ComponentCallbacks.FlagGainCallback | |
| CB_FLAGLOST | SS.Core.ComponentCallbacks.FlagLostCallback | |
| CB_FLAGONMAP | SS.Core.ComponentCallbacks.FlagOnMapCallback |  |
| CB_FLAGRESET | SS.Core.ComponentCallbacks.FlagGameResetCallback | |
| CB_GLOBALCONFIGCHANGED | SS.Core.ComponentCallbacks.GlobalConfigChangedCallback | |
| CB_GOAL | SS.Core.ComponentCallbacks.BallGoalCallback<br>SS.Core.ComponentCallbacks.BallGameGoalCallback | |
| CB_GREEN | SS.Core.ComponentCallbacks.GreenCallback | |
| CB_INTERVAL_ENDED | SS.Core.ComponentCallbacks.IPersistIntervalEndedCallback | |
| CB_KILL | SS.Core.ComponentCallbacks.KillCallback | |
| CB_KOTH_END |SS.Core.ComponentCallbacks.KothEndedCallback | |
| CB_KOTH_PLAYER_WIN | SS.Core.ComponentCallbacks.KothWonCallback | |
| CB_KOTH_PLAYER_WIN_END | n/a | Use KothWonCallback instead. |
| CB_KOTH_START | SS.Core.ComponentCallbacks.KothStartedCallback | |
| CB_LOGFUNC | SS.Core.ComponentCallbacks.LogCallback | |
| CB_MAINLOOP | SS.Core.ComponentCallbacks.MainloopCallback | The mainloop is implemented differently than in ASSS. Better options exist depending on what you need: IMainloopTimer, IServerTimer, use IMainloop to queue a workitem, do your own waiting on a worker thread (e.g. LagAction module). |
| CB_NEWPLAYER | SS.Core.ComponentCallbacks.NewPlayerCallback | |
| CB_PLAYERACTION | SS.Core.ComponentCallbacks.PlayerActionCallback | |
| CB_PLAYERDAMAGE | SS.Core.ComponentCallbacks.PlayerDamageCallback | |
| CB_PPK | SS.Core.ComponentCallbacks.PlayerPositionCallback | |
| CB_PRESHIPFREQCHANGE | SS.Core.ComponentCallbacks.PreShipFreqChangeCallback | |
| CB_REGION | SS.Core.ComponentCallbacks.MapRegionCallback | |
| CB_REWRITECOMMAND | | |
| CB_SAFEZONE | SS.Core.ComponentCallbacks.SafeZoneCallback | |
| CB_SET_BANNER | SS.Core.ComponentCallbacks.SetBannerCallback | |
| CB_SHIPFREQCHANGE | SS.Core.ComponentCallbacks.ShipFreqChangeCallback | |
| CB_SPAWN | SS.Core.ComponentCallbacks.SpawnCallback | |
| CB_TIMESUP | SS.Core.ComponentCallbacks.GameTimerEndedCallback<br>SS.Core.ComponentCallbacks.GameTimerChangedCallback | |
| CB_TURFTAG | SS.Core.ComponentCallbacks.StaticFlagClaimedCallback | |
| CB_WARP | SS.Core.ComponentCallbacks.WarpCallback | |
| CB_WARZONEWIN | n/a | |

## Advisors
| ASSS  | Subspace Server .NET | Notes |
| --- | --- | --- |
| Aballs | SS.Core.ComponentAdvisors.IBallsAdvisor | |
| Aenforcer | SS.Core.ComponentAdvisors.IFreqManagerEnforcerAdvisor | |
| Akill | SS.Core.ComponentAdvisors.IKillAdvisor | |
| Appk | SS.Core.ComponentAdvisors.IPlayerPositionAdvisor | |
