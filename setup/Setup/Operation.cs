using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Terraria.ModLoader.Setup
{
	public abstract class Operation
	{
		protected delegate void Worker();


		protected class Future
		{
			public readonly Worker worker;

			public Future(Worker worker) {
				this.worker = worker;
			}
		}


		protected void ExecuteParallel(List<Future> items, int maxConcurrentTasks = 0) {
			maxConcurrentTasks = maxConcurrentTasks > 0 ? maxConcurrentTasks : Environment.ProcessorCount;

			Parallel.ForEach(
				Partitioner.Create(items, EnumerablePartitionerOptions.NoBuffering),
				new ParallelOptions { MaxDegreeOfParallelism = maxConcurrentTasks },
				item => item.worker()
			);
		}


		public abstract void Run();
	}
}