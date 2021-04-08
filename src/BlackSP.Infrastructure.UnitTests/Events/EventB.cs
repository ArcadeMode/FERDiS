﻿using BlackSP.Kernel.Models;
using System;

namespace BlackSP.Infrastructure.UnitTests.Events
{
    public class EventB : IEvent
    {
        public int? Key { get; set; }

        public DateTime EventTime { get; set; }
    }
}
