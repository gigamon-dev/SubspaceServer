using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    public enum ShipType
    {
        Warbird = 0,
        Javelin,
        Spider,
        Leviathan,
        Terrier,
        Weasel,
        Lancaster,
        Shark,
        Spec
    }

    public enum ChatSound : byte
    {
        None = 0, 
        Beep1, 
        Beep2, 
        NoATT, 
        Violent, 
        Hallellula, 
        Reagan, 
        Inconcievable, 
        Churchill, 
        Listen, 
        Crying, 
        Burp, 
        Girl, 
        Scream, 
        Fart1, 
        Fart2, 
        Phone, 
        WorldAttack, 
        Gibberish, 
        Ooo, 
        Gee, 
        Ohh, 
        Aww, 
        GameSucks, 
        Sheep, 
        CantLogin, 
        Beep3, 
        MusicLoop = 100, 
        MusicStop, 
        MusicOnce, 
        Ding, 
        Goal
    }

    public enum Prize
    {
        Recharge = 1, 
        Energy, 
        Rotation, 
        Stealth, 
        Cloak, 
        XRadar, 
        Warp, 
        Gun, 
        Bomb, 
        Bounce, 
        Thrust, 
        Speed, 
        FullCharge, 
        Shutdown, 
        Multifire, 
        Prox, 
        Super, 
        Shield, 
        Shrap, 
        Antiwarp, 
        Repel, 
        Burst, 
        Decoy, 
        Thor, 
        Multiprize, 
        Brick, 
        Rocket, 
        Portal
    }
}
