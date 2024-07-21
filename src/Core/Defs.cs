namespace SS.Core
{
    public enum ExitCode : byte
    {
        /// <summary>
        /// Normal shutdown
        /// </summary>
        None = 0,

        /// <summary>
        /// Recycle
        /// </summary>
        Recycle = 1,

        /// <summary>
        /// General 'something went wrong' error
        /// </summary>
        General = 2,

        /// <summary>
        /// we ran out of memory
        /// </summary>
        //Memory = 3,

        /// <summary>
        /// The initial module file is missing
        /// </summary>
        ModConf = 4,

        /// <summary>
        /// An error loading initial modules
        /// </summary>
        ModLoad = 5,
    }

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
}
