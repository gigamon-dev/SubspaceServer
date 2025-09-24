namespace SS.Replay
{
    public enum ReplayState
    {
        /// <summary>
        /// Idle, nothing is being recorded or played back.
        /// </summary>
        None,

        /// <summary>
        /// A replay is being recorded.
        /// </summary>
        Recording,

        /// <summary>
        /// A replay is being played back.
        /// </summary>
        Playing,
    }
}
