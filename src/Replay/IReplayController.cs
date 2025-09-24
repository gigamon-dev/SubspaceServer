using SS.Core;

namespace SS.Replay
{
	/// <summary>
	/// Interface to control playback and recording of replays.
	/// </summary>
    /// <remarks>
    /// Only one replay can be recording or playing in an arena at a time.
    /// </remarks>
	public interface IReplayController : IComponentInterface
	{
		/// <summary>
		/// Gets the current replay state.
		/// </summary>
		/// <param name="arena">The arena to get the replay state for.</param>
		/// <returns>The current state.</returns>
		ReplayState GetState(Arena arena);

		/// <summary>
		/// Starts recording a replay.
		/// </summary>
		/// <remarks>
		/// This only submits a task to record. It does not wait for recording to begin.
		/// </remarks>
		/// <param name="arena">The arena to start recording the replay in.</param>
		/// <param name="filePath">The file name and path to save the recording to.</param>
		/// <param name="comments">Optional descriptive information added as comments into the recording.</param>
		/// <returns><see langword="true"/> if a task to start a recording was submitted. <see langword="false"/> if in the wrong state to start recording.</returns>
		bool StartRecording(Arena arena, string filePath, string? comments);

        /// <summary>
        /// Ends recording of a replay.
        /// </summary>
		/// <remarks>
		/// This only tells the recording task to end. It does not wait for the recording to end.
		/// </remarks>
        /// <param name="arena">The arena to end recording in.</param>
        /// <returns><see langword="true"/> if the recording was told to end. <see langword="false"/> if in the wrong state to stop recording.</returns>
        bool StopRecording(Arena arena);

        /// <summary>
        /// Starts playback of a replay.
        /// </summary>
		/// <remarks>
		/// This only submits a task to do the playback. It does not wait for the playback to begin.
		/// </remarks>
        /// <param name="arena">The arena to start playabck in.</param>
        /// <param name="filePath">The file name and path of reecording to play.</param>
        /// <returns><see langword="true"/> if a task to start a playback was submitted. <see langword="false"/> if in the wrong state to start playback.</returns>
        bool StartPlayback(Arena arena, string filePath);

        /// <summary>
        /// Pauses playback of a replay.
        /// </summary>
        /// <remarks>
        /// This only sends a command to the playback task. It does not wait for the playback to actually pause.
        /// </remarks>
        /// <param name="arena">The arena to pause playback in.</param>
        /// <returns><see langword="true"/> if a command was submitted to stop playback. <see langword="false"/> if in the wrong state to pause playback.</returns>
        bool PausePlayback(Arena arena);

        /// <summary>
        /// Resume playback of a replay.
        /// </summary>
        /// <remarks>
        /// This only sends a command to the playback task. It does not wait for the playback to actually resume.
        /// </remarks>
        /// <param name="arena">The arena to resume playback in.</param>
        /// <returns><see langword="true"/> if a command was submitted to resume playback. <see langword="false"/> if in the wrong state to resume playback.</returns>
        bool ResumePlayback(Arena arena);

        /// <summary>
        /// Stop playback of a replay.
        /// </summary>
        /// <remarks>
        /// This only sends a command to the playback task. It does not wait for the playback to actually stop.
        /// </remarks>
        /// <param name="arena">The arena to stop playback in.</param>
        /// <returns><see langword="true"/> if a command was submitted to stop playback. <see langword="false"/> if in the wrong state to stop playback.</returns>
		bool StopPlayback(Arena arena);
	}
}
