using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq.Expressions;
using System.Collections;
using System.Reflection.Emit;
using System.Linq;

namespace Terraria.ModLoader.Core
{
	public class HookList<T> where T : GlobalType
	{
		public readonly MethodInfo method;

		private int[] registeredGlobalIndices = new int[0];

		internal HookList(MethodInfo method) {
			this.method = method;
		}

		[Obsolete]
		public IEnumerable<T> Enumerate(IEntityWithGlobals<T> entity) => Enumerate(entity.Globals);

		[Obsolete]
		public IEnumerable<T> Enumerate(RefReadOnlyArray<Instanced<T>> instances) => Enumerate(instances.array);

		[Obsolete]
		public IEnumerable<T> Enumerate(Instanced<T>[] instances) {
			if (instances.Length == 0) {
				yield break;
			}

			int i = 0;
			var instance = instances[i];

			foreach (int globalIndex in registeredGlobalIndices) {
				while (instance.index < globalIndex) {
					if (++i == instances.Length)
						yield break;

					instance = instances[i];
				}

				if (instance.index == globalIndex) {
					yield return instance.instance;
				}
			}
		}

		public void Update(IList<T> instances) => registeredGlobalIndices = ModLoader.BuildGlobalHookNew(instances, method);
	}

	public class HookList<TGlobal, TDelegate> : HookList<TGlobal>
		where TGlobal : GlobalType
		where TDelegate : Delegate
	{
		private static class Test
		{
			private static readonly HookList<GlobalItem, Action<Item>> TestHook = new HookList<GlobalItem, Action<Item>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.PostUpdate)),
				//Invocation
				enumerator => (Item item) => {
					item.ModItem?.PostUpdate();

					foreach (var g in enumerator(item.globalItems)) {
						g.PostUpdate(item);
					}
				}
			);

			private static void TestHookGoal(Instanced<GlobalItem>[] instances, int[] registeredGlobalIndices, Item item) {
				item.ModItem?.PostUpdate();

				if (instances.Length > 0) {
					int i = 0;
					var instance = instances[i];

					foreach (int globalIndex in registeredGlobalIndices) {
						while (instance.index < globalIndex) {
							if (++i == instances.Length)
								goto End;

							instance = instances[i];
						}

						if (instance.index == globalIndex) {
							instance.instance.PostUpdate(item);
						}
					}
				}

				End:;
			}
		}

		public delegate IEnumerable<TGlobal> Enumerator(RefReadOnlyArray<Instanced<TGlobal>> globals);

		public TDelegate Invoke { get; private set; }

		public HookList(MethodInfo method, Func<Enumerator, TDelegate> getInvoker) : base(method) {
			Invoke = GenerateInvocationDelegate(getInvoker);
		}

		internal HookList(Expression<Func<TGlobal, TDelegate>> method, Func<Enumerator, TDelegate> getInvoker) : base(ModLoader.Method(method)) {
			Invoke = GenerateInvocationDelegate(getInvoker);
		}

		private TDelegate GenerateInvocationDelegate(Func<Enumerator, TDelegate> getOriginal) {
			var dummyEnumerator = (Enumerator)DummyEnumerator;
			var original = getOriginal(dummyEnumerator);

			throw new NotImplementedException();
		}

		private static IEnumerable<TGlobal> DummyEnumerator(RefReadOnlyArray<Instanced<TGlobal>> globals) => throw new NotImplementedException();
	}
}
