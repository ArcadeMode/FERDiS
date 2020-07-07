﻿using BlackSP.InMemory.Configuration;
using BlackSP.Kernel.Endpoints;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlackSP.InMemory.Core
{
    public class InputEndpointHost
    {
        private readonly IInputEndpoint _inputEndpoint;
        private readonly ConnectionTable _connectionTable;

        public InputEndpointHost(IInputEndpoint inputEndpoint, ConnectionTable connectionTable)
        {
            _inputEndpoint = inputEndpoint ?? throw new ArgumentNullException(nameof(inputEndpoint));
            _connectionTable = connectionTable ?? throw new ArgumentNullException(nameof(connectionTable));
        }

        /// <summary>
        /// Launches threads for each incoming connection
        /// </summary>
        /// <returns></returns>
        public async Task Start(string instanceName, string endpointName, CancellationToken token)
        {
            var incomingStreams = _connectionTable.GetIncomingStreams(instanceName, endpointName);
            var incomingConnections = _connectionTable.GetIncomingConnections(instanceName, endpointName);

            var threads = new List<Task>();
            for(var i = 0; i < incomingConnections.Length; i++)
            {
                int shardId = i;
                Stream s = incomingStreams[shardId];
                Connection c = incomingConnections[shardId];
                threads.Add(Task.Run(() => IngressWithRestart(instanceName, endpointName, shardId, 3, TimeSpan.FromSeconds(10), token)));
            }

            try
            {
                await await Task.WhenAny(threads);
            }
            catch(OperationCanceledException)
            {
                foreach(var stream in incomingStreams)
                {
                    stream.Dispose(); //force close the stream to trigger exception in output endpoint as if it were a dropped network stream
                }
                foreach(var connection in incomingConnections)
                {
                    _connectionTable.RegisterConnection(connection); //re-register connection to create new streams around a failed instance
                }
            }
        }

        private async Task IngressWithRestart(string instanceName, string endpointName, int shardId, int maxRestarts, TimeSpan restartTimeout, CancellationToken token)
        {
            while (true)
            {
                Stream s = null;
                Connection c = null;
                try
                {
                    s = _connectionTable.GetIncomingStreams(instanceName, endpointName)[shardId];
                    c = _connectionTable.GetIncomingConnections(instanceName, endpointName)[shardId];
                   
                    Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName} starting.\t(from {c.FromInstanceName}${c.FromEndpointName}${c.FromShardId})");
                    await _inputEndpoint.Ingress(s, c.FromEndpointName, c.FromShardId, token);
                    Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName} exited without exceptions");
                    
                    return;
                }
                catch(OperationCanceledException)
                {
                    Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName} exiting due to cancellation");
                    throw;
                }
                catch (Exception)
                {
                    if (maxRestarts-- == 0)
                    {
                        Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName} exited with exceptions, no restart: exceeded maxRestarts.");
                        throw;
                    }
                    Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName} exited with exceptions, restart in {restartTimeout.TotalSeconds} seconds.");
                    await Task.Delay(restartTimeout);
                }
            }
        }
    }
}
