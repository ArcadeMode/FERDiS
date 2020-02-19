﻿using BlackSP.Interfaces.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlackSP.Interfaces.Endpoints
{
    public interface IInputEndpoint
    {

        /// <summary>
        /// Fetches deserialized event from input channel
        /// </summary>
        /// <returns></returns>
        IEvent GetNext();

        /// <summary>
        /// Check if input channel has any deserialized input ready
        /// </summary>
        /// <returns></returns>
        bool HasInput();

        Task Ingress(Stream s, CancellationToken t);
    }
}
