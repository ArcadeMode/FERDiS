﻿using BlackSP.Core.Processors;
using BlackSP.Core.Models;
using BlackSP.Core.Monitors;
using BlackSP.Kernel.MessageProcessing;
using BlackSP.Kernel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlackSP.Core.Extensions;
using BlackSP.Core.Handlers;
using BlackSP.Infrastructure.Layers.Data;
using BlackSP.Infrastructure.Layers.Control.Payloads;
using BlackSP.Kernel;
using System.Diagnostics;

namespace BlackSP.Infrastructure.Layers.Control.Handlers
{
    /// <summary>
    /// ControlMessage handler dedicated to handling requests on the worker side
    /// </summary>
    public class WorkerRequestHandler : ForwardingPayloadHandlerBase<ControlMessage, WorkerRequestPayload>, IDisposable
    {
        private readonly IVertexConfiguration _vertexConfiguration;
        private readonly DataMessageProcessor _processor;
        private readonly ConnectionMonitor _connectionMonitor;
        private readonly ISource<DataMessage> _dataSource;
        private readonly ILogger _logger;

        private CancellationTokenSource _ctSource;
        private Task _activeThread;
        private bool upstreamFullyConnected;
        private bool downstreamFullyConnected;
        private bool disposedValue;

        public WorkerRequestHandler(DataMessageProcessor processor,
                                    ConnectionMonitor connectionMonitor,
                                    ISource<DataMessage> source,
                                    IVertexConfiguration vertexConfiguration,  
                                    ILogger logger)
        {            
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _connectionMonitor = connectionMonitor ?? throw new ArgumentNullException(nameof(connectionMonitor));
            _dataSource = source ?? throw new ArgumentNullException(nameof(source));
            _vertexConfiguration = vertexConfiguration ?? throw new ArgumentNullException(nameof(vertexConfiguration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            upstreamFullyConnected = downstreamFullyConnected = false;
            _connectionMonitor.OnConnectionChange += ConnectionMonitor_OnConnectionChange;
        }

        protected override async Task<IEnumerable<ControlMessage>> Handle(WorkerRequestPayload payload)
        {
            _ = payload ?? throw new ArgumentNullException(nameof(payload));

            await PerformRequestedAction(payload).ConfigureAwait(false);

            var response = new ControlMessage();
            response.AddPayload(new WorkerResponsePayload()
            {
                OriginInstanceName = _vertexConfiguration.InstanceName,
                UpstreamFullyConnected = upstreamFullyConnected,
                DownstreamFullyConnected = downstreamFullyConnected,
                DataProcessActive = _activeThread != null,
                OriginalRequestType = payload.RequestType
            });
            return response.Yield();
        }

        private async Task PerformRequestedAction(WorkerRequestPayload payload)
        {
            var requestType = payload.RequestType;
            try
            {
                Task action = null;
                switch (requestType)
                {
                    case WorkerRequestType.Status:
                        _logger.Information("Processing status request");
                        action = Task.CompletedTask;
                        break;
                    case WorkerRequestType.StartProcessing:
                        action = StartDataProcess();
                        break;
                    case WorkerRequestType.StopProcessing:
                        action = StopDataProcess(payload.UpstreamHaltingInstances);
                        break;
                    default:
                        throw new InvalidOperationException($"Received worker request \"{requestType}\" which is not implemented in {this.GetType()}");
                }
                await action.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.Fatal(e, $"Exception in {this.GetType()} while handling request of type \"{requestType}\"");
                throw;
            }
        }

        private void ConnectionMonitor_OnConnectionChange(ConnectionMonitor sender, ConnectionMonitorEventArgs e)
        {
            upstreamFullyConnected = e.UpstreamFullyConnected;
            downstreamFullyConnected = e.DownstreamFullyConnected;
        }

        private Task StartDataProcess()
        {
            if (_activeThread == null)
            {
                _ctSource = new CancellationTokenSource();
                _activeThread = _processor.StartProcess(_ctSource.Token);
                _logger.Information($"Data processor started by coordinator instruction");
            }
            else
            {
                _logger.Warning($"Data processor already started, cannot start again");
            }
            return Task.CompletedTask;
        }

        private async Task StopDataProcess(IEnumerable<string> upstreamHaltedInstances)
        {
            if (_activeThread != null)
            {
                var sw = new Stopwatch();
                
                _logger.Fatal($"Data processor stop instruction received by coordinator"); //TODO: make debug level
                sw.Start();
                await CancelProcessingAndResetLocally().ConfigureAwait(false);
                _logger.Fatal($"Data processor was successfully stopped in {sw.ElapsedMilliseconds}ms, proceeding with network flush with upstream halting instances"); //TODO: make debug level
                sw.Restart();
                await _dataSource.Flush(upstreamHaltedInstances ?? Enumerable.Empty<string>()).ConfigureAwait(false);
                _logger.Fatal($"Network flush completed in {sw.ElapsedMilliseconds}ms, halt request completed successfully"); //TODO: make debug level
                sw.Stop();
            }
            else
            {
                _logger.Warning($"Data processor already stopped, cannot stop again");
            }
        }

        private async Task CancelProcessingAndResetLocally()
        {
            try
            {
                _ctSource.Cancel();
                await _activeThread.ConfigureAwait(false);
                _ctSource.Dispose();
            }
            catch (OperationCanceledException) { /* silence cancellation exceptions, these are expected. */}
            finally
            {
                _activeThread = null;
                _ctSource = null;
            }
        }

        #region dispose support

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if(_activeThread != null)
                    {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                        CancelProcessingAndResetLocally();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    }
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~DataLayerControllerMiddleware()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
