﻿using BlackSP.Core.Exceptions;
using BlackSP.Core.Extensions;
using BlackSP.Kernel.Configuration;
using BlackSP.Kernel.MessageProcessing;
using BlackSP.Kernel.Models;
using BlackSP.Kernel.Serialization;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace BlackSP.Core.MessageProcessing
{
    /// <summary>
    /// Receives input from any source. Exposes received messages through the ISource interface.<br/>
    /// Sorts and orders input based on message types to be consumed one-by-one.
    /// </summary>
    public sealed class TestMessageReceiverSource<TMessage> : IReceiverSource<TMessage>, IDisposable
        where TMessage : class, IMessage
    {
        public (IEndpointConfiguration, int) MessageOrigin { get; private set; }


        private readonly IObjectSerializer _serializer;
        private readonly IVertexConfiguration _vertexConfiguration;
        private readonly ILogger _logger;
        
        /// <summary>
        /// dict mapping connection keys to origin pairs
        /// </summary>
        private IDictionary<string, (IEndpointConfiguration, int)> _originDictionary;

        /// <summary>
        /// dict mapping connection keys to cancellation token sources to cancel an input
        /// </summary>
        private IDictionary<string, CancellationTokenSource> _connectionCancellationDictionary;

        /// <summary>
        /// dict mapping connection keys to flush completion sources
        /// </summary>
        private IDictionary<string, TaskCompletionSource<bool>> _flushDictionary;

        /// <summary>
        /// internal dict for block/unblock api
        /// </summary>
        private IDictionary<string, SemaphoreSlim> _blockDictionary;

        /// <summary>
        /// internal dict for blocking channels for priority management
        /// </summary>
        private IDictionary<string, SemaphoreSlim> _priorityAccessDictionary;

        /// <summary>
        /// internal dict for synchronization between Take and Receive calls
        /// </summary>
        private IDictionary<string, SemaphoreSlim> _takeBeforeDeliverDictionary;

        /// <summary>
        /// semaphore to allow a single producer to take priority
        /// </summary>
        private SemaphoreSlim _priorityAccess;

        /// <summary>
        /// semaphore to allow a single producer into the reception critical section
        /// </summary>
        private SemaphoreSlim _nextMessageAccess;


        private Channel<(TMessage, IEndpointConfiguration, int)> _messages;


        private bool disposedValue;

        private (TMessage, IEndpointConfiguration, int) _lastTake { get; set; }
        private (TMessage, IEndpointConfiguration, int) _lastWrite { get; set; }

        public TestMessageReceiverSource(IObjectSerializer serializer, IVertexConfiguration vertexConfiguration, ILogger logger)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _vertexConfiguration = vertexConfiguration ?? throw new ArgumentNullException(nameof(vertexConfiguration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            _originDictionary = new Dictionary<string, (IEndpointConfiguration, int)>();
            _flushDictionary = new Dictionary<string, TaskCompletionSource<bool>>();
            
            _blockDictionary = new Dictionary<string, SemaphoreSlim>();
            _priorityAccessDictionary = new Dictionary<string, SemaphoreSlim>();
            _takeBeforeDeliverDictionary = new Dictionary<string, SemaphoreSlim>();
            _connectionCancellationDictionary = new Dictionary<string, CancellationTokenSource>();
            _priorityAccess = new SemaphoreSlim(1, 1);
            _nextMessageAccess = new SemaphoreSlim(1, 1);

            _messages = Channel.CreateBounded<(TMessage, IEndpointConfiguration, int)>(Constants.DefaultThreadBoundaryQueueSize);// new BufferBlock<(TMessage, IEndpointConfiguration, int)>(new DataflowBlockOptions { BoundedCapacity = 1 });

            InitialiseDataStructures();
        }

        public async Task<TMessage> Take(CancellationToken t)
        {
            var (msg, origin, shard) = await _messages.Reader.ReadAsync(t).ConfigureAwait(false);
            MessageOrigin = (origin, shard);
            return msg;
        }

        public async Task Receive(byte[] message, IEndpointConfiguration origin, int shardId, CancellationToken t)
        {
            _ = message ?? throw new ArgumentNullException(nameof(message));
            _ = origin ?? throw new ArgumentNullException(nameof(origin));
            var connectionKey = origin.GetConnectionKey(shardId);

            var accessed = new List<SemaphoreSlim>();
            var lastDeliveryTakenAccess = _takeBeforeDeliverDictionary[connectionKey];
            var ct = _connectionCancellationDictionary[connectionKey].Token;
            try
            {
                t.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
                using var lcts = CancellationTokenSource.CreateLinkedTokenSource(t, ct);

                //do deserialization
                var dserializedMsg = await _serializer.DeserializeAsync<TMessage>(message, lcts.Token).ConfigureAwait(false);

                //use critical section
                var triplet = (dserializedMsg, origin, shardId);
                await _messages.Writer.WriteAsync(triplet, lcts.Token);
            }
            catch(OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.Debug($"Receive aborted on connectionKey: {connectionKey}");
                if(lastDeliveryTakenAccess.CurrentCount == 0)
                {   //cancelled while waiting for Take, as it wont be taken, release
                    lastDeliveryTakenAccess.Release();
                }
                ThrowIfFlushInProgress(origin, shardId);
                throw new ReceptionCancelledException("Message reception was cancelled, retry is allowed");
            }
            finally
            {
                //release any acquired semaphores
                accessed.ForEach(sema => sema.Release());
            }
        }

        public async Task Flush(IEnumerable<string> instanceNamesToFlush)
        {
            _ = instanceNamesToFlush ?? throw new ArgumentNullException(nameof(instanceNamesToFlush));
            var flushes = new List<Task>();
            foreach(var (endpoint, shardId) in _originDictionary.Values)
            {
                var connectionKey = endpoint.GetConnectionKey(shardId);

                if (instanceNamesToFlush.Contains(endpoint.GetRemoteInstanceName(shardId)))
                {
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _flushDictionary.Add(connectionKey, tcs);
                    flushes.Add(tcs.Task);
                } 
                _connectionCancellationDictionary[connectionKey].Cancel(); //cancel any ongoing calls to the Receive method (without
            }

            if(flushes.Count != instanceNamesToFlush.Count())
            {
                throw new ArgumentException($"Invalid instanceName in enumerable: {string.Join(", ", instanceNamesToFlush)}", nameof(instanceNamesToFlush));
            }

            if(flushes.Any())
            {
                _logger.Debug($"Receiver flushing {flushes.Count}/{_originDictionary.Count} queues");
                await Task.WhenAll(flushes).ConfigureAwait(false);
                _logger.Debug($"Receiver flushed {flushes.Count}/{_originDictionary.Count} queues");

                
            } 
            else
            {
                _logger.Debug($"Receiver did not have to flush any connections");
            }

            foreach (var key in _connectionCancellationDictionary.Keys.ToArray()) //after flushing re-allow connections to call Receive
            {
                var prevSource = _connectionCancellationDictionary[key];
                _connectionCancellationDictionary[key] = new CancellationTokenSource();
                prevSource.Dispose();
            }
        }

        public void ThrowIfFlushInProgress(IEndpointConfiguration origin, int shardId)
        {
            _ = origin ?? throw new ArgumentNullException(nameof(origin));
            if (_flushDictionary.ContainsKey(origin.GetConnectionKey(shardId))) //check if flush in progress
            {
                throw new FlushInProgressException();
            }
        }

        public void CompleteFlush(IEndpointConfiguration origin, int shardId)
        {
            _ = origin ?? throw new ArgumentNullException(nameof(origin));
            var connectionKey = origin.GetConnectionKey(shardId);
            _logger.Debug($"Completing flush result on {connectionKey}");
            
            //complete flush task so waiting Flush() call eventually returns
            _flushDictionary.Get(connectionKey).SetResult(true);
            _flushDictionary.Remove(connectionKey);

            //reset cancellationtoken to prevent new connection auto-cancel (DONE AFTER FLUSH)
            //_connectionCancellationDictionary[connectionKey] = new CancellationTokenSource();

            //check "last write" if it belongs to this endpoint and if so, delete it (prevent lingering message after flush)
            var (msg, lorigin, lshard) = _lastWrite;
            if (msg != null && lorigin.GetConnectionKey(lshard) == origin.GetConnectionKey(shardId)) {
                //last written item was from this channel, do reset to get rid of it

                _messages.Writer.Complete();
                _messages = Channel.CreateBounded<(TMessage, IEndpointConfiguration, int)>(Constants.DefaultThreadBoundaryQueueSize);// new BufferBlock<(TMessage, IEndpointConfiguration, int)>(new DataflowBlockOptions { BoundedCapacity = 1 });


                _logger.Debug("Receiver channel reset due to flush completion on connectionKey: " + origin.GetConnectionKey(shardId));
                _lastWrite = default;
            }

            (msg, lorigin, lshard) = _lastTake;
            if(msg != null && lorigin.GetConnectionKey(lshard) == origin.GetConnectionKey(shardId)) {
                //last taken item was from flushed connection, so there is no task awaiting for release
                _lastTake = default; //reset so the next Take call will not exceed this connection's "_takeBeforeDeliverDictionary" semaphore.
            }
        }

        public async Task TakePriority(IEndpointConfiguration prioOrigin, int shardId)
        {
            _ = prioOrigin ?? throw new ArgumentNullException(nameof(prioOrigin));
            var prioKey = prioOrigin.GetConnectionKey(shardId);
            var tasks = new List<Task>();

            await _priorityAccess.WaitAsync().ConfigureAwait(false);

            foreach (var (origin, shard) in _originDictionary.Values)
            {
                var key = origin.GetConnectionKey(shard);
                if (key == prioKey)
                {
                    continue;
                }
                tasks.Add(_priorityAccessDictionary.Get(key).WaitAsync());
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public void ReleasePriority(IEndpointConfiguration prioOrigin, int shardId)
        {
            _ = prioOrigin ?? throw new ArgumentNullException(nameof(prioOrigin));
            var prioKey = prioOrigin.GetConnectionKey(shardId);
            foreach (var (origin, shard) in _originDictionary.Values)
            {
                var key = origin.GetConnectionKey(shard);
                if (key == prioKey)
                {   //skip self
                    continue;
                }
                _priorityAccessDictionary.Get(key).Release();
            }
            _priorityAccess.Release();
        }
        public async Task Block(IEndpointConfiguration origin, int shardId)
        {
            _ = origin ?? throw new ArgumentNullException(nameof(origin));
            //wait associated semaphore
            var connectionKey = origin.GetConnectionKey(shardId);
            var semaphore = _blockDictionary[connectionKey];
            await semaphore.WaitAsync().ConfigureAwait(false);
        }

        public void Unblock(IEndpointConfiguration origin, int shardId)
        {
            _ = origin ?? throw new ArgumentNullException(nameof(origin));
            //release associated semaphore
            var connectionKey = origin.GetConnectionKey(shardId);
            var semaphore = _blockDictionary[connectionKey];
            semaphore.Release();
            
        }

        private void InitialiseDataStructures()
        {
            foreach (var config in _vertexConfiguration.InputEndpoints)
            {
                for (int i = 0; i < config.RemoteInstanceNames.Count(); i++)
                {
                    int shardId = i;
                    var connectionKey = config.GetConnectionKey(shardId);
                    _originDictionary[connectionKey] = (config, shardId);
                    _blockDictionary[connectionKey] = new SemaphoreSlim(1, 1);
                    _priorityAccessDictionary[connectionKey] = new SemaphoreSlim(1, 1);
                    _takeBeforeDeliverDictionary[connectionKey] = new SemaphoreSlim(1, 1);
                    _connectionCancellationDictionary[connectionKey] = new CancellationTokenSource();
                }
            }
        }

#region dispose pattern
        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

#endregion
    }
}
