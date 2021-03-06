using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Harmony
{
	public class PatchProcessor
	{
		static object locker = new object();

		readonly HarmonyInstance instance;

		readonly Type container;
		readonly HarmonyMethod containerAttributes;

		List<MethodBase> originals = new List<MethodBase>();
		HarmonyMethod prefix;
		HarmonyMethod postfix;
		HarmonyMethod transpiler;

		public PatchProcessor(HarmonyInstance instance, Type type, HarmonyMethod attributes)
		{
			this.instance = instance;
			container = type;
			containerAttributes = attributes ?? new HarmonyMethod(null);
			prefix = containerAttributes.Clone();
			postfix = containerAttributes.Clone();
			transpiler = containerAttributes.Clone();
			PrepareType();
		}

		public PatchProcessor(HarmonyInstance instance, List<MethodBase> originals, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod transpiler = null)
		{
			this.instance = instance;
			this.originals = originals;
			this.prefix = prefix ?? new HarmonyMethod(null);
			this.postfix = postfix ?? new HarmonyMethod(null);
			this.transpiler = transpiler ?? new HarmonyMethod(null);
		}

		public static Patches GetPatchInfo(MethodBase method)
		{
			lock (locker)
			{
				var patchInfo = HarmonySharedState.GetPatchInfo(method);
				if (patchInfo == null) return null;
				return new Patches(patchInfo.prefixes, patchInfo.postfixes, patchInfo.transpilers);
			}
		}

		public static IEnumerable<MethodBase> AllPatchedMethods()
		{
			lock (locker)
			{
				return HarmonySharedState.GetPatchedMethods();
			}
		}

		public void Patch()
		{
			lock (locker)
			{
				foreach (var original in originals)
				{
					if (original == null)
						throw new NullReferenceException("original");

					var individualPrepareResult = RunMethod<HarmonyPrepare, bool>(true, original);
					if (individualPrepareResult)
					{
						var patchInfo = HarmonySharedState.GetPatchInfo(original);
						if (patchInfo == null) patchInfo = new PatchInfo();

						PatchFunctions.AddPrefix(patchInfo, instance.Id, prefix);
						PatchFunctions.AddPostfix(patchInfo, instance.Id, postfix);
						PatchFunctions.AddTranspiler(patchInfo, instance.Id, transpiler);
						PatchFunctions.UpdateWrapper(original, patchInfo, instance.Id);

						HarmonySharedState.UpdatePatchInfo(original, patchInfo);

						RunMethod<HarmonyCleanup>(original);
					}
				}
			}
		}

		public void Unpatch(HarmonyPatchType type, string harmonyID)
		{
			lock (locker)
			{
				foreach (var original in originals)
				{
					var patchInfo = HarmonySharedState.GetPatchInfo(original);
					if (patchInfo == null) patchInfo = new PatchInfo();

					if (type == HarmonyPatchType.All || type == HarmonyPatchType.Prefix)
						PatchFunctions.RemovePrefix(patchInfo, harmonyID);
					if (type == HarmonyPatchType.All || type == HarmonyPatchType.Postfix)
						PatchFunctions.RemovePostfix(patchInfo, harmonyID);
					if (type == HarmonyPatchType.All || type == HarmonyPatchType.Transpiler)
						PatchFunctions.RemoveTranspiler(patchInfo, harmonyID);
					PatchFunctions.UpdateWrapper(original, patchInfo, instance.Id);

					HarmonySharedState.UpdatePatchInfo(original, patchInfo);
				}
			}
		}

		public void Unpatch(MethodInfo patch)
		{
			lock (locker)
			{
				foreach (var original in originals)
				{
					var patchInfo = HarmonySharedState.GetPatchInfo(original);
					if (patchInfo == null) patchInfo = new PatchInfo();

					PatchFunctions.RemovePatch(patchInfo, patch);
					PatchFunctions.UpdateWrapper(original, patchInfo, instance.Id);

					HarmonySharedState.UpdatePatchInfo(original, patchInfo);
				}
			}
		}

		void PrepareType()
		{
			var mainPrepareResult = RunMethod<HarmonyPrepare, bool>(true);
			if (mainPrepareResult == false)
				return;

			var customOriginals = RunMethod<HarmonyTargetMethods, IEnumerable<MethodBase>>(null);
			if (customOriginals != null)
			{
				originals = customOriginals.ToList();
			}
			else
			{
				var isPatchAll = Attribute.GetCustomAttribute(container, typeof(HarmonyPatchAll)) != null;
				if (isPatchAll)
				{
					var type = containerAttributes.originalType;
					originals.AddRange(AccessTools.GetDeclaredConstructors(type).Cast<MethodBase>());
					originals.AddRange(AccessTools.GetDeclaredMethods(type).Cast<MethodBase>());
				}
				else
				{
					var original = GetOriginalMethod();
					if (original == null)
						original = RunMethod<HarmonyTargetMethod, MethodBase>(null);
					if (original != null)
						originals.Add(original);
					else
						throw new ArgumentException("No target method specified for class " + container.FullName);
				}
			}

			PatchTools.GetPatches(container, out prefix.method, out postfix.method, out transpiler.method);

			if (prefix.method != null)
			{
				if (prefix.method.IsStatic == false)
					throw new ArgumentException("Patch method " + prefix.method.FullDescription() + " must be static");

				var prefixAttributes = prefix.method.GetHarmonyMethods();
				containerAttributes.Merge(HarmonyMethod.Merge(prefixAttributes)).CopyTo(prefix);
			}

			if (postfix.method != null)
			{
				if (postfix.method.IsStatic == false)
					throw new ArgumentException("Patch method " + postfix.method.FullDescription() + " must be static");

				var postfixAttributes = postfix.method.GetHarmonyMethods();
				containerAttributes.Merge(HarmonyMethod.Merge(postfixAttributes)).CopyTo(postfix);
			}

			if (transpiler.method != null)
			{
				if (transpiler.method.IsStatic == false)
					throw new ArgumentException("Patch method " + transpiler.method.FullDescription() + " must be static");

				var infixAttributes = transpiler.method.GetHarmonyMethods();
				containerAttributes.Merge(HarmonyMethod.Merge(infixAttributes)).CopyTo(transpiler);
			}
		}

		MethodBase GetOriginalMethod()
		{
			var attr = containerAttributes;
			if (attr.originalType == null) return null;
			if (attr.methodName == null)
				return AccessTools.Constructor(attr.originalType, attr.parameter);
			return AccessTools.Method(attr.originalType, attr.methodName, attr.parameter);
		}

		T RunMethod<S, T>(T defaultIfNotExisting, params object[] parameters)
		{
			if (container == null)
				return defaultIfNotExisting;

			var methodName = typeof(S).Name.Replace("Harmony", "");

			var paramList = new List<object> { instance };
			paramList.AddRange(parameters);
			var paramTypes = AccessTools.GetTypes(paramList.ToArray());
			var method = PatchTools.GetPatchMethod<S>(container, methodName, paramTypes);
			if (method != null && typeof(T).IsAssignableFrom(method.ReturnType))
				return (T)method.Invoke(null, paramList.ToArray());

			method = PatchTools.GetPatchMethod<S>(container, methodName, new Type[] { typeof(HarmonyInstance) });
			if (method != null && typeof(T).IsAssignableFrom(method.ReturnType))
				return (T)method.Invoke(null, new object[] { instance });

			method = PatchTools.GetPatchMethod<S>(container, methodName, Type.EmptyTypes);
			if (method != null)
			{
				if (typeof(T).IsAssignableFrom(method.ReturnType))
					return (T)method.Invoke(null, Type.EmptyTypes);

				method.Invoke(null, Type.EmptyTypes);
				return defaultIfNotExisting;
			}

			return defaultIfNotExisting;
		}

		void RunMethod<S>(params object[] parameters)
		{
			if (container == null)
				return;

			var methodName = typeof(S).Name.Replace("Harmony", "");

			var paramList = new List<object> { instance };
			paramList.AddRange(parameters);
			var paramTypes = AccessTools.GetTypes(paramList.ToArray());
			var method = PatchTools.GetPatchMethod<S>(container, methodName, paramTypes);
			if (method != null)
			{
				method.Invoke(null, paramList.ToArray());
				return;
			}

			method = PatchTools.GetPatchMethod<S>(container, methodName, new Type[] { typeof(HarmonyInstance) });
			if (method != null)
			{
				method.Invoke(null, new object[] { instance });
				return;
			}

			method = PatchTools.GetPatchMethod<S>(container, methodName, Type.EmptyTypes);
			if (method != null)
			{
				method.Invoke(null, Type.EmptyTypes);
				return;
			}
		}
	}
}