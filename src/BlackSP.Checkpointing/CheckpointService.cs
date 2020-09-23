﻿using BlackSP.Checkpointing.Extensions;
using BlackSP.Checkpointing.Core;
using BlackSP.Kernel.Checkpointing;
using BlackSP.Serialization.Extensions;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BlackSP.Checkpointing.Persistence;
using BlackSP.Checkpointing.Exceptions;
using System.Threading.Tasks;
using Serilog;
using System.Collections.Immutable;
using BlackSP.Kernel.Models;
using BlackSP.Checkpointing.Models;

namespace BlackSP.Checkpointing
{
    ///<inheritdoc/>
    public class CheckpointService : ICheckpointService
    {

        private readonly ObjectRegistry _register;
        private readonly CheckpointDependencyTracker _dpTracker;
        private readonly RecoveryLineCalculator.Factory _rlCalcFactory;
        private readonly ICheckpointStorage _storage;
        private readonly ICheckpointConfiguration _checkpointConfiguration;
        private readonly ILogger _logger;

        public CheckpointService(ObjectRegistry register, 
                                 CheckpointDependencyTracker dependencyTracker,
                                 RecoveryLineCalculator.Factory rlCalcFactory,
                                 ICheckpointStorage checkpointStorage, 
                                 ICheckpointConfiguration checkpointConfiguration,
                                 ILogger logger)
        {
            _register = register ?? throw new ArgumentNullException(nameof(register));
            _dpTracker = dependencyTracker ?? throw new ArgumentNullException(nameof(dependencyTracker));
            _storage = checkpointStorage ?? throw new ArgumentNullException(nameof(checkpointStorage));
            _rlCalcFactory = rlCalcFactory ?? throw new ArgumentNullException(nameof(rlCalcFactory));
            _checkpointConfiguration = checkpointConfiguration ?? throw new ArgumentNullException(nameof(checkpointConfiguration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        ///<inheritdoc/>
        public void UpdateCheckpointDependency(string originInstanceName, Guid checkpointId)
        {
            _dpTracker.UpdateDependency(originInstanceName, checkpointId);
        }

        public Guid GetLatestCheckpointId(string currentInstanceName)
        {
            return _dpTracker.Dependencies[currentInstanceName];
        }

        public Guid GetSecondLastCheckpointId(string currentInstanceName)
        {
            return _dpTracker.GetPreviousDependency(currentInstanceName);
        }

        ///<inheritdoc/>
        public async Task<IRecoveryLine> CalculateRecoveryLine(IEnumerable<string> failedInstanceNames)
        {
            var allCheckpointMetadatas = await _storage.GetAllMetaData().ConfigureAwait(false);
            var calculator = _rlCalcFactory.Invoke(allCheckpointMetadatas);
            return calculator.CalculateRecoveryLine(_checkpointConfiguration.AllowReusingState, failedInstanceNames);
        }

        ///<inheritdoc/>
        public bool RegisterObject(object o)
        {
            _ = o ?? throw new ArgumentNullException(nameof(o));

            var type = o.GetType();
            var identifier = type.AssemblyQualifiedName; //Note: currently the implementation supports only one instance of any concrete type 
            //Under the assumption of exact same object registration order (at least regarding instances of the same type) this could be extended to support multiple instances
            if(_register.Contains(identifier))
            {
                throw new NotSupportedException($"Registering multiple instances of the same type is not supported");
            }
            
            if(!o.AssertCheckpointability())
            {
                return false;
            }
            _logger.Verbose($"Object of type {type} is registered for checkpointing with {nameof(CheckpointService)}.");
            _register.Add(identifier, o);
            return true;
        }

        ///<inheritdoc/>
        public async Task<Guid> TakeCheckpoint(string currentInstanceName)
        {
            var snapshots = _register.TakeObjectSnapshots();
            var metadata = new MetaData(Guid.NewGuid(), _dpTracker.Dependencies, currentInstanceName, DateTime.UtcNow);
            var checkpoint = new Checkpoint(metadata, snapshots);
            await _storage.Store(checkpoint).ConfigureAwait(false);
            _dpTracker.UpdateDependency(currentInstanceName, checkpoint.Id); //ensure next checkpoint depends on current
            return checkpoint.Id;
        }

        ///<inheritdoc/>
        public async Task TakeInitialCheckpointIfNotExists(string currentInstanceName)
        {
            var allCheckpointMetadatas = await _storage.GetAllMetaData().ConfigureAwait(false);
            //there is a checkpoint taken by this instance, without any dependencies on its own older checkpoints so it must be the initial one
            var allCheckpointsByInstance = allCheckpointMetadatas.Where(m => m.InstanceName == currentInstanceName);
            var firstCheckpoint = allCheckpointsByInstance.FirstOrDefault(m => !m.Dependencies.ContainsKey(currentInstanceName));
            if (firstCheckpoint != null)
            {
                _logger.Information($"Skipping initial checkpoint, it already exists");
                var lastCheckpoint = allCheckpointMetadatas.OrderBy(m => m.CreatedAtUtc).Last();
                _dpTracker.OverwriteDependencies(lastCheckpoint.Dependencies); //ensure next checkpoint inherits dependencies from latest checkpoint
                _dpTracker.UpdateDependency(currentInstanceName, lastCheckpoint.Id);
                return;
            }
            
            try
            {
                _logger.Information($"Taking checkpoint of initial state");
                await TakeCheckpoint(currentInstanceName).ConfigureAwait(false);
                _logger.Information($"Initial checkpoint successfully taken");
            } 
            catch(Exception e)
            {
                _logger.Warning("Failed to take initial checkpoint, rethrowing exception");
                throw;
            }
        }

        public Guid GetLastCheckpointId(string currentInstanceName)
        {
            return _dpTracker.Dependencies[currentInstanceName];
        }

        ///<inheritdoc/>
        public async Task RestoreCheckpoint(Guid checkpointId)
        {
            var checkpoint = (await _storage.Retrieve(checkpointId).ConfigureAwait(false)) 
                ?? throw new CheckpointRestorationException($"Checkpoint storage returned null for checkpoint ID: {checkpointId}");
            _register.RestoreCheckpoint(checkpoint);
            _dpTracker.OverwriteDependencies(checkpoint.MetaData.Dependencies);
            //TODO: delete all checkpoints after (and/or before?) current checkpoint
        }

    }
}
