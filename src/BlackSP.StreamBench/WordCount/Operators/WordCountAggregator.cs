﻿using BlackSP.Kernel.Operators;
using BlackSP.StreamBench.WordCount.Events;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BlackSP.StreamBench.WordCount.Operators
{
    public class WordCountAggregator : IAggregateOperator<WordEvent, WordEvent>
    {
        public static int WindowSizeSeconds = 5;
        public TimeSpan WindowSize => TimeSpan.FromSeconds(WindowSizeSeconds);
        public TimeSpan WindowSlideSize => TimeSpan.FromSeconds(WindowSizeSeconds);

        private readonly ILogger _logger;

        public WordCountAggregator(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public IEnumerable<WordEvent> Aggregate(IEnumerable<WordEvent> window)
        {
            var wordGroups = window.GroupBy(ev => ev.Word);
            _logger.Debug($"Aggregating {wordGroups.Count()} different words");
            foreach (var group in wordGroups)
            {
                if(group.Any(ev => ev.Count == 0 || ev.EventTime == default))
                {
                    throw new InvalidProgramException("u fooken wot?");
                }
                yield return new WordEvent
                {
                    EventTime = group.First().EventTime,
                    Word = group.Key,
                    Count = group.Sum(ev => ev.Count)
                };
            }
        }
    }
}
