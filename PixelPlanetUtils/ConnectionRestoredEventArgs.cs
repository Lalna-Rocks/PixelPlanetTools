﻿using System;

namespace PixelPlanetUtils
{
    public class ConnectionRestoredEventArgs : EventArgs
    {
        public TimeSpan OfflinePeriod { get; }

        public ConnectionRestoredEventArgs(DateTime disconnectionTime)
        {
            OfflinePeriod = DateTime.Now - disconnectionTime;
        }
    }
}