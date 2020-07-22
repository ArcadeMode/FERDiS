﻿using BlackSP.Checkpointing.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BlackSP.Checkpointing.UnitTests.Models
{
    public class ClassA
    {

        [Checkpointable]
        private string _value;

        [Checkpointable]
        private ICollection<int> _ints;

        public ClassA(string value)
        {
            _value = value ?? throw new ArgumentNullException(nameof(value));
            _ints = new List<int>();
        }

        public void Append(string value)
        {
            _value += value;
        }

        public string GetValue()
        {
            return _value;
        }

        public void Add(int i)
        {
            _ints.Add(i);
        }

        public int GetTotal()
        {
            return _ints.Sum();
        }
    }
}
