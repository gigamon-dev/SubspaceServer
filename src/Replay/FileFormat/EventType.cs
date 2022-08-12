﻿namespace SS.Replay.FileFormat
{
    public enum EventType
    {
        //
        // The following are events the base ASSS implementation has.
        //

        Null,
        Enter,
        Leave,
        ShipChange,
        FreqChange,
        Kill,
        Chat,
        Position,
        Packet, // ASSS has this in playback, but does seem to record it, so skipped.

        //
        // The following are matched up with the modifications of the record module from Powerball Zone.
        //

        Brick, // single brick only
        BallFire, // did not implement
        BallCatch, // did not implement
        BallPacket,
        BallGoal, // did not implement
        ArenaMessage, // did not implement, used Chat instead

        //
        // The following are specific to this server.
        //

        CrownToggleOn = 100,
        CrownToggleOff = 101,
        // TODO: flag events?
        // TODO: bricks (allow multiple, allow specifying a time to allow for placing initial bricks)
    }
}