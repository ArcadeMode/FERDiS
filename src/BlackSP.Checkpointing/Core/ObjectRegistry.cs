﻿using BlackSP.Checkpointing.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BlackSP.Checkpointing.Core
{

    /// <summary>
    /// Container to store references to all registered stateful objects
    /// </summary>
    public class ObjectRegistry
    {

        private readonly IDictionary<string, object> _state;

        public IEnumerable<KeyValuePair<string, object>> Objects => _state;

        public ObjectRegistry()
        {
            _state = new Dictionary<string, object>();
        }

        /// <summary>
        /// Add a new object to the ObjectRegister
        /// </summary>
        /// <param name="t"></param>
        /// <param name="o"></param>
        public void Add(string key, object o)
        {
            _state.Add(key, o);
        }

        public bool Contains(string key)
        {
            return _state.ContainsKey(key);
        }

        public Checkpoint TakeCheckpoint()
        {
            var cpDict = new Dictionary<string, ObjectSnapshot>();
            foreach (var kvp in _state)
            {
                var identifier = kvp.Key;
                var obj = kvp.Value;

                var snapshot = ObjectSnapshot.TakeSnapshot(obj);
                cpDict.Add(identifier, snapshot);
            }
            return new Checkpoint(Guid.NewGuid(), cpDict);
        }

        public void RestoreCheckpoint(Checkpoint checkpoint)
        {
            _ = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));

            if(!CanRestore(checkpoint)) 
            {
                //this is bad, the checkpoint should contain each and every object in the registered state
                throw new CheckpointRestorationException("Missing object(s) in ");
            }

            //insert/overwrite state deserialized state object
        }

        public bool CanRestore(Checkpoint cp)
        {
            var keys = _state.Keys;
            if (keys.Intersect(cp.Keys).Count() == keys.Count())
            {
                return true;
            }
            return false;
        }
    }
}
