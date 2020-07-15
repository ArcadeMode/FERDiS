﻿using BlackSP.Simulator.Configuration;
using BlackSP.Kernel.Endpoints;
using Nerdbank.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlackSP.Simulator.Core
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
        public async Task Start(string instanceName, string endpointName, CancellationToken t)
        {
            var incomingStreams = _connectionTable.GetIncomingStreams(instanceName, endpointName);
            var incomingConnections = _connectionTable.GetIncomingConnections(instanceName, endpointName);

            var hostCTSource = new CancellationTokenSource();
            var linkedCTSource = CancellationTokenSource.CreateLinkedTokenSource(t, hostCTSource.Token);
            var threads = new List<Task>();
            for(var i = 0; i < incomingConnections.Length; i++)
            {
                int shardId = i;
                Stream s = incomingStreams[shardId];
                Connection c = incomingConnections[shardId];
                threads.Add(Task.Run(() => IngressWithRestart(instanceName, endpointName, shardId, 99, TimeSpan.FromSeconds(5), linkedCTSource.Token)));
            }

            try
            {
                await await Task.WhenAny(threads); //exited means the thread broke out of the restart loop
            }
            catch(OperationCanceledException)
            {
                try
                {
                    linkedCTSource.Cancel();
                    await Task.WhenAll(threads);
                } catch(OperationCanceledException e) { /*shh*/}
                
                
                Console.WriteLine($"{instanceName} - Input endpoint {endpointName} was cancelled and is now resetting streams");
                foreach (var stream in incomingStreams)
                {
                    stream.Dispose(); //force close the stream to trigger exception in output endpoint as if it were a dropped network stream
                }
                foreach(var connection in incomingConnections)
                {
                    _connectionTable.RegisterConnection(connection); //re-register connection to create new streams around a failed instance
                }
                // ????  await Task.WhenAll(threads); //threads must all stop during cancellation..
                throw;
            } 
            finally
            {
                Console.WriteLine($"{instanceName} - exiting input host {endpointName}");
            }
        }

        private async Task IngressWithRestart(string instanceName, string endpointName, int shardId, int maxRestarts, TimeSpan restartTimeout, CancellationToken t)
        {
            while (!t.IsCancellationRequested)
            {
                Stream s = null;
                Connection c = null;
                try
                {
                    t.ThrowIfCancellationRequested();

                    s = _connectionTable.GetIncomingStreams(instanceName, endpointName)[shardId];
                    c = _connectionTable.GetIncomingConnections(instanceName, endpointName)[shardId];
                   
                    Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName}${shardId} starting.\t(remote {c.FromInstanceName}${c.FromEndpointName}${c.FromShardId})");
                    await _inputEndpoint.Ingress(s, c.FromEndpointName, c.FromShardId, t);
                    Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName}${shardId} exited without exceptions");                    
                    return;
                }
                catch(OperationCanceledException)
                {
                    Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName}${shardId} exiting due to cancellation");
                    throw;
                }
                catch (Exception)
                {
                    if (maxRestarts-- == 0)
                    {
                        Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName}${shardId} exited with exceptions, no restart: exceeded maxRestarts.");
                        throw;
                    }
                    Console.WriteLine($"{c.ToInstanceName} - Input endpoint {c.ToEndpointName}${shardId} exited with exceptions, restart in {restartTimeout.TotalSeconds} seconds.");
                    await Task.Delay(restartTimeout, t);
                }
            }
            t.ThrowIfCancellationRequested();
        }
    }
}
