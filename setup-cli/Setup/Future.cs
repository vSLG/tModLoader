using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Terraria.ModLoader.Setup
{
	public class Future
	{
		// TODO: status

		public delegate void Worker();
		public readonly Worker worker;

		public Future(Worker worker) {
			this.worker = worker;
		}

		static public void ExecuteParallel(List<Future> items, int maxConcurrentTasks = 0) {
			maxConcurrentTasks = maxConcurrentTasks > 0 ? maxConcurrentTasks : Environment.ProcessorCount;

			Parallel.ForEach(
				Partitioner.Create(items, EnumerablePartitionerOptions.NoBuffering),
				new ParallelOptions { MaxDegreeOfParallelism = maxConcurrentTasks },
				item => item.worker() // TODO: print progress
			);
		}
	}
}