// Util.cs
//
// Author:
//   Lluis Sanchez Gual <lluis@novell.com>
//
// Copyright (c) 2008 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.SymbolStore;
using System.Reflection;
using System.Text;

using Mono.Debugging.Backend;
using Mono.Debugging.Client;
using Mono.Debugging.Evaluation;

using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using CorApi.ComInterop;

using CorApi2.Extensions;
using CorApi2.Metadata;
using CorApi2.Metadata.Microsoft.Samples.Debugging.CorMetadata;

namespace Mono.Debugging.Win32
{
	public unsafe class CorObjectAdaptor: ObjectValueAdaptor
	{
		public override bool IsPrimitive (EvaluationContext ctx, object val)
		{
			object v = GetRealObject (ctx, val);
			return (v is ICorDebugGenericValue) || (v is ICorDebugStringValue);
		}

		public override bool IsPointer (EvaluationContext ctx, object val)
		{
			ICorDebugType type = (ICorDebugType) GetValueType (ctx, val);
			return IsPointer (type);
		}

		public override bool IsEnum (EvaluationContext ctx, object val)
		{
			if (!(val is CorValRef))
				return false;
			ICorDebugType type = (ICorDebugType) GetValueType (ctx, val);
			return IsEnum (ctx, type);
		}

		public override bool IsArray (EvaluationContext ctx, object val)
		{
			return GetRealObject (ctx, val) is ICorDebugArrayValue;
		}
		
		public override bool IsString (EvaluationContext ctx, object val)
		{
			return GetRealObject (ctx, val) is ICorDebugStringValue;
		}

		public override bool IsNull(EvaluationContext ctx, object gval)
		{
			if(gval == null)
				return true;
			var val = gval as CorValRef;
			if(val == null)
				return true;
			if(val.Val == null)
				return true;
			if(val.Val is ICorDebugReferenceValue)
			{
				int bNull = 0;
				((ICorDebugReferenceValue)val.Val).IsNull(&bNull).AssertSucceeded("((ICorDebugReferenceValue) val.Val).IsNull(&bNull)");
				if(bNull != 0)
					return true;
			}

			ICorDebugValue obj = GetRealObject(ctx, val);
			if(!(obj is ICorDebugReferenceValue))
				return false;
			{
				int bNull;
				((ICorDebugReferenceValue)obj).IsNull(&bNull).AssertSucceeded("((ICorDebugReferenceValue)obj).IsNull(&bNull)");
				return bNull != 0;
			}
		}

		public override bool IsValueType(object type)
		{
			CorElementType elementtype;
			((ICorDebugType)type).GetType(out elementtype).AssertSucceeded("((ICorDebugType)type).GetType(out elementtype)");
			return elementtype == CorElementType.ELEMENT_TYPE_VALUETYPE;
		}

		public override bool IsClass (EvaluationContext ctx, object type)
		{
			var t = (ICorDebugType) type;
			CorElementType ty;
			t.GetType(out ty).AssertSucceeded("t.GetType(out ty)");
			var cctx = (CorEvaluationContext)ctx;
			Type tt;
			if (ty == CorElementType.ELEMENT_TYPE_STRING ||
			   ty == CorElementType.ELEMENT_TYPE_ARRAY ||
			   ty == CorElementType.ELEMENT_TYPE_SZARRAY)
				return true;
			// Primitive check
			if (MetadataHelperFunctionsExtensions.CoreTypes.TryGetValue (ty, out tt))
				return false;

			if (IsIEnumerable (t, cctx.Session))
				return false;

			if(ty == CorElementType.ELEMENT_TYPE_CLASS)
			{
				ICorDebugClass @class;
				t.GetClass(out @class).AssertSucceeded("t.GetClass(out @class)");
				if(@class != null)
					return true;
			}
			return IsValueType (t);
		}

		public override bool IsGenericType (EvaluationContext ctx, object type)
		{
			return (((ICorDebugType)type).Type() == CorElementType.ELEMENT_TYPE_GENERICINST) || base.IsGenericType (ctx, type);
		}

		public override bool NullableHasValue (EvaluationContext ctx, object type, object obj)
		{
			ValueReference hasValue = GetMember (ctx, type, obj, "hasValue");

			return (bool) hasValue.ObjectValue;
		}

		public override ValueReference NullableGetValue (EvaluationContext ctx, object type, object obj)
		{
			return GetMember (ctx, type, obj, "value");
		}

		public override string GetTypeName (EvaluationContext ctx, object gtype)
		{
			ICorDebugType type = (ICorDebugType) gtype;
			CorEvaluationContext cctx = (CorEvaluationContext) ctx;
			Type t;
			if (MetadataHelperFunctionsExtensions.CoreTypes.TryGetValue (type.Type(), out t))
				return t.FullName;
			try {
				if (type.Type() == CorElementType.ELEMENT_TYPE_ARRAY || type.Type() == CorElementType.ELEMENT_TYPE_SZARRAY)
				{
					uint nRank;
					type.GetRank(&nRank).AssertSucceeded("Could not get the Rank of a Type.");
					ICorDebugType firsttypeparam;
					type.GetFirstTypeParameter(out firsttypeparam).AssertSucceeded("Could not get the First Type Parameter of a Type.");
					return GetTypeName (ctx, firsttypeparam) + "[" + new string (',', (int)nRank - 1) + "]";
				}

				if (type.Type() == CorElementType.ELEMENT_TYPE_BYREF)
				{
					ICorDebugType firsttypeparam;
					type.GetFirstTypeParameter(out firsttypeparam).AssertSucceeded("Could not get the First Type Parameter of a Type.");
					return GetTypeName (ctx, firsttypeparam) + "&";
				}

				if (type.Type() == CorElementType.ELEMENT_TYPE_PTR)
				{
					ICorDebugType firsttypeparam;
					type.GetFirstTypeParameter(out firsttypeparam).AssertSucceeded("Could not get the First Type Parameter of a Type.");
					return GetTypeName (ctx, firsttypeparam) + "*";
				}

				return type.GetTypeInfo (cctx.Session).FullName;
			}
			catch (Exception ex) {
				DebuggerLoggingService.LogError ("Exception in GetTypeName()", ex);
				return "[Unknown type]";
			}
		}

		public override object GetValueType (EvaluationContext ctx, object val)
		{
			if (val == null)
				return GetType (ctx, "System.Object");

			var realObject = GetRealObject (ctx, val);
			if (realObject == null)
				return GetType (ctx, "System.Object");;
			return realObject.GetExactType();
		}
		
		public override object GetBaseType (EvaluationContext ctx, object type)
		{
			ICorDebugType basetype;
			((ICorDebugType) type).GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
			return basetype;
		}

		protected override object GetBaseTypeWithAttribute (EvaluationContext ctx, object type, object attrType)
		{
			var wctx = (CorEvaluationContext) ctx;
			var attr = ((ICorDebugType) attrType).GetTypeInfo (wctx.Session);
			var tm = type as ICorDebugType;

			while (tm != null) {
				var t = tm.GetTypeInfo (wctx.Session);

				if (t.GetCustomAttributes (attr, false).Any ())
					return tm;

				ICorDebugType basetype;
				tm.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
				tm = basetype;
			}

			return null;
		}

		[SuppressMessage("ReSharper", "CoVariantArrayConversion")]
		public override object[] GetTypeArgs (EvaluationContext ctx, object type)
		{
			return ((ICorDebugType)type).TypeParameters();
		}

		static IEnumerable<Type> GetAllTypes (EvaluationContext gctx)
		{
			CorEvaluationContext ctx = (CorEvaluationContext) gctx;
			foreach (ICorDebugModule mod in ctx.Session.GetAllModules()) {
				CorMetadataImport mi = ctx.Session.GetMetadataForModule (mod);
				if (mi != null) {
					foreach (Type t in mi.DefinedTypes)
						yield return t;
				}
			}
		}

		readonly Dictionary<string, ICorDebugType> nameToTypeCache = new Dictionary<string, ICorDebugType> ();
		readonly Dictionary<ICorDebugType, string> typeToNameCache = new Dictionary<ICorDebugType, string> ();
		readonly HashSet<string> unresolvedNames = new HashSet<string> ();


		string GetCacheName (string name, ICorDebugType[] typeArgs)
		{
			if (typeArgs == null || typeArgs.Length == 0)
				return name;
			var result = new StringBuilder(name + "<");
			for (int i = 0; i < typeArgs.Length; i++) {
				string currentTypeName;
				if (!typeToNameCache.TryGetValue (typeArgs[i], out currentTypeName)) {
					DebuggerLoggingService.LogMessage ("Can't get cached name for generic type {0} because it's substitution type isn't found in cache", name);
					return null; //Unable to resolve? Don't cache. This should never happen.
				}
				result.Append (currentTypeName);
				if (i < typeArgs.Length - 1)
					result.Append (",");
			}
			result.Append (">");
			return result.ToString();
		}

		public override object GetType(EvaluationContext gctx, string name, object[] gtypeArgs)
		{
			if(string.IsNullOrEmpty(name))
				return null;
			ICorDebugType[] typeArgs = CastArray<ICorDebugType>(gtypeArgs);
			var ctx = (CorEvaluationContext)gctx;
			ICorDebugFunction framefunction;
			ctx.Frame.GetFunction(out framefunction).AssertSucceeded("Could not get the Function of a Frame.");
			ICorDebugClass framefunctionclass;
			framefunction.GetClass(out framefunctionclass).AssertSucceeded("framefunction.GetClass(out framefunctionclass)");
			ICorDebugModule callingModule;
			framefunctionclass.GetModule(out callingModule).AssertSucceeded("framefunctionclass.GetModule(out callingModule)");
			ICorDebugAssembly callingmoduleassembly;
			callingModule.GetAssembly(out callingmoduleassembly).AssertSucceeded("callingModule.GetAssembly(out callingmoduleassembly)");
			ICorDebugAppDomain callingDomain;
			callingmoduleassembly.GetAppDomain(out callingDomain).AssertSucceeded("callingmoduleassembly.GetAppDomain(out callingDomain)");
			uint nCallingDomainId = 0;
			callingDomain.GetID(&nCallingDomainId).AssertSucceeded("callingDomain.GetID(&nCallingDomainId)");
			string domainPrefixedName = string.Format("{0}:{1}", nCallingDomainId, name);
			string cacheName = GetCacheName(domainPrefixedName, typeArgs);
			ICorDebugType typeFromCache;

			if(!string.IsNullOrEmpty(cacheName) && nameToTypeCache.TryGetValue(cacheName, out typeFromCache))
				return typeFromCache;
			if(unresolvedNames.Contains(cacheName ?? domainPrefixedName))
				return null;
			foreach(ICorDebugModule mod in ctx.Session.GetModules(callingDomain))
			{
				CorMetadataImport mi = ctx.Session.GetMetadataForModule(mod);
				if(mi != null)
				{
					int token = mi.GetTypeTokenFromName(name);
					if(token == CorMetadataImport.TokenNotFound)
						continue;
					Type t = mi.GetType(((uint)token));
					ICorDebugClass cls;
					mod.GetClassFromToken(((uint)t.MetadataToken), out cls).AssertSucceeded("mod.GetClassFromToken (((uint)t.MetadataToken), out cls)");
					ICorDebugType foundType = cls.GetParameterizedType(CorElementType.ELEMENT_TYPE_CLASS, typeArgs);
					if(foundType != null)
					{
						if(!string.IsNullOrEmpty(cacheName))
						{
							nameToTypeCache[cacheName] = foundType;
							typeToNameCache[foundType] = cacheName;
						}
						return foundType;
					}
				}
			}
			unresolvedNames.Add(cacheName ?? domainPrefixedName);
			return null;
		}

		static T[] CastArray<T> (object[] array)
		{
			if (array == null)
				return null;
			T[] ret = new T[array.Length];
			Array.Copy (array, ret, array.Length);
			return ret;
		}

        public override string CallToString(EvaluationContext ctx, object objr)
        {
            ICorDebugValue obj = GetRealObject(ctx, objr);

	        if(obj is ICorDebugReferenceValue)
	        {
		        int isNull = 0;
		        Com.QueryInteface<ICorDebugReferenceValue>(obj).IsNull(&isNull).AssertSucceeded("Com.QueryInteface<ICorDebugReferenceValue>(obj).IsNull(&isNull)");
		        if(isNull != 0)
			        return string.Empty;
	        }

	        var stringVal = obj as ICorDebugStringValue;
            if (stringVal != null)
                return LpcwstrHelper.GetString(stringVal.GetString, "Could not get the String Value string.");

			var genericVal = obj as ICorDebugGenericValue;
			if (genericVal != null)
			{
				return genericVal.GetValue ().ToString ();
			}

	        var arr = obj as ICorDebugArrayValue;
	        if(arr != null)
	        {
		        var arradaptor = new ArrayAdaptor(ctx, new CorValRef<ICorDebugArrayValue>(arr));
		        ICorDebugType firsttypeparam;
		        arr.GetExactType().GetFirstTypeParameter(out firsttypeparam).AssertSucceeded("Could not get the First Type Parameter of a Type.");
		        var tn = new StringBuilder(GetDisplayTypeName(ctx, firsttypeparam));
		        tn.Append("[");
		        int[] dims = arradaptor.GetDimensions();
		        for(int n = 0; n < dims.Length; n++)
		        {
			        if(n > 0)
				        tn.Append(',');
			        tn.Append(dims[n]);
		        }
		        tn.Append("]");
		        return tn.ToString();
	        }

	        var cctx = (CorEvaluationContext)ctx;
            var co = obj as ICorDebugObjectValue;
            if (co != null)
            {
                if (IsEnum (ctx, co.GetExactType()))
                {
                    MetadataType rt = co.GetExactType().GetTypeInfo(cctx.Session) as MetadataType;
                    bool isFlags = rt != null && rt.ReallyIsFlagsEnum;
                    string enumName = GetTypeName(ctx, co.GetExactType());
                    ValueReference val = GetMember(ctx, null, objr, "value__");
                    ulong nval = (ulong)System.Convert.ChangeType(val.ObjectValue, typeof(ulong));
                    ulong remainingFlags = nval;
                    string flags = null;
                    foreach (ValueReference evals in GetMembers(ctx, co.GetExactType(), null, BindingFlags.Public | BindingFlags.Static))
                    {
                        ulong nev = (ulong)System.Convert.ChangeType(evals.ObjectValue, typeof(ulong));
                        if (nval == nev)
                            return evals.Name;
                        if (isFlags && nev != 0 && (nval & nev) == nev)
                        {
                            if (flags == null)
                                flags = enumName + "." + evals.Name;
                            else
                                flags += " | " + enumName + "." + evals.Name;
                            remainingFlags &= ~nev;
                        }
                    }
                    if (isFlags)
                    {
                        if (remainingFlags == nval)
                            return nval.ToString ();
                        if (remainingFlags != 0)
                            flags += " | " + remainingFlags;
                        return flags;
                    }
                    else
                        return nval.ToString ();
                }

				var targetType = (ICorDebugType)GetValueType (ctx, objr);

				var methodInfo = OverloadResolve (cctx, "ToString", targetType, new ICorDebugType[0], BindingFlags.Public | BindingFlags.Instance, false);
				if (methodInfo != null && methodInfo.Item1.DeclaringType != null && methodInfo.Item1.DeclaringType.FullName != "System.Object") {
		            var args = new object[0];
					object ores = RuntimeInvoke (ctx, targetType, objr, "ToString", new object[0], args, args);
					var res = GetRealObject (ctx, ores) as ICorDebugStringValue;
                    if (res != null)
                        return LpcwstrHelper.GetString(res.GetString, "Could not get the String Value string value.");
                }

				return GetDisplayTypeName (ctx, targetType);
            }

            return base.CallToString(ctx, obj);
        }

		public override object CreateTypeObject (EvaluationContext ctx, object type)
		{
			var t = (ICorDebugType)type;
			ICorDebugClass typeclass;
			t.GetClass(out typeclass).AssertSucceeded("Could not get the Class of a Type.");
			ICorDebugModule classmodule;
			typeclass.GetModule(out classmodule).AssertSucceeded("Could not get the Module of a Class.");
			ICorDebugAssembly assembly;
			classmodule.GetAssembly(out assembly).AssertSucceeded("classmodule.GetAssembly(out assembly)");
			string tname = GetTypeName (ctx, t) + ", " + System.IO.Path.GetFileNameWithoutExtension (LpcwstrHelper.GetString(assembly.GetName, "Could not get the Assembly Name."));
			var stype = (ICorDebugType) GetType (ctx, "System.Type");
			object[] argTypes = { GetType (ctx, "System.String") };
			object[] argVals = { CreateValue (ctx, tname) };
			return RuntimeInvoke (ctx, stype, null, "GetType", argTypes, argVals);
		}

		public CorValRef GetBoxedArg (CorEvaluationContext ctx, CorValRef val, Type argType)
		{
			// Boxes a value when required
			if (argType == typeof (object) && IsValueType (ctx, val))
				return Box (ctx, val);
			else
				return val;
		}

		private static bool IsValueType(CorEvaluationContext ctx, CorValRef val)
		{
			ICorDebugValue v = GetRealObject(ctx, val);
			if(v is ICorDebugGenericValue)
				return true;
			CorElementType eltype = 0;
			v.GetType(&eltype).AssertSucceeded("v.GetType(&eltype)");
			return eltype == CorElementType.ELEMENT_TYPE_VALUETYPE;
		}

		CorValRef Box (CorEvaluationContext ctx, CorValRef val)
		{
			CorValRef arr = new CorValRef (delegate {
				return ctx.Session.NewArray (ctx, (ICorDebugType)GetValueType (ctx, val), 1);
			});
			ArrayAdaptor realArr = new ArrayAdaptor (ctx, new CorValRef<ICorDebugArrayValue> (() => (ICorDebugArrayValue) GetRealObject (ctx, arr)));
			realArr.SetElement (new [] { 0 }, val);
			ICorDebugType at = (ICorDebugType) GetType (ctx, "System.Array");
			object[] argTypes = { GetType (ctx, "System.Int32") };
			return (CorValRef)RuntimeInvoke (ctx, at, arr, "GetValue", argTypes, new object[] { CreateValue (ctx, 0) });
		}

		public override bool HasMethod (EvaluationContext gctx, object gtargetType, string methodName, object[] ggenericArgTypes, object[] gargTypes, BindingFlags flags)
		{
			// FIXME: support generic methods by using the genericArgTypes parameter
			ICorDebugType targetType = (ICorDebugType) gtargetType;
			ICorDebugType[] argTypes = gargTypes != null ? CastArray<ICorDebugType> (gargTypes) : null;
			CorEvaluationContext ctx = (CorEvaluationContext)gctx;
			flags = flags | BindingFlags.Public | BindingFlags.NonPublic;

			return OverloadResolve (ctx, methodName, targetType, argTypes, flags, false) != null;
		}

		public override object RuntimeInvoke (EvaluationContext gctx, object gtargetType, object gtarget, string methodName, object[] ggenericArgTypes, object[] gargTypes, object[] gargValues)
		{
			// FIXME: support generic methods by using the genericArgTypes parameter
			ICorDebugType targetType = (ICorDebugType) gtargetType;
			CorValRef target = (CorValRef) gtarget;
			ICorDebugType[] argTypes = CastArray<ICorDebugType> (gargTypes);
			CorValRef[] argValues = CastArray<CorValRef> (gargValues);

			BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic;
			if (target != null)
				flags |= BindingFlags.Instance;
			else
				flags |= BindingFlags.Static;

			CorEvaluationContext ctx = (CorEvaluationContext)gctx;
			var methodInfo = OverloadResolve (ctx, methodName, targetType, argTypes, flags, true);
			if (methodInfo == null)
				return null;
			var method = methodInfo.Item1;
			var methodOwner = methodInfo.Item2;
			ParameterInfo[] parameters = method.GetParameters ();
			// TODO: Check this.
			for (int n = 0; n < parameters.Length; n++) {
				if (parameters[n].ParameterType == typeof(object) && IsValueType (ctx, argValues[n]) && !IsEnum (ctx, argValues[n]))
					argValues[n] = Box (ctx, argValues[n]);
			}

			CorValRef v = new CorValRef (delegate {
				ICorDebugModule mod = null;
				if (methodOwner.Type() == CorElementType.ELEMENT_TYPE_ARRAY || methodOwner.Type() == CorElementType.ELEMENT_TYPE_SZARRAY
					|| MetadataHelperFunctionsExtensions.CoreTypes.ContainsKey (methodOwner.Type())) {
					ICorDebugClass typeclass;
					((ICorDebugType) ctx.Adapter.GetType (ctx, "System.Object")).GetClass(out typeclass).AssertSucceeded("Could not get the Class of a Type.");
					ICorDebugModule classmodule;
					typeclass.GetModule(out classmodule).AssertSucceeded("Could not get the Module of a Class.");
					mod = classmodule;
				}
				else {
					ICorDebugClass typeclass;
					methodOwner.GetClass(out typeclass).AssertSucceeded("Could not get the Class of a Type.");
					ICorDebugModule classmodule;
					typeclass.GetModule(out classmodule).AssertSucceeded("Could not get the Module of a Class.");
					mod = classmodule;
				}
				ICorDebugFunction func;
				mod.GetFunctionFromToken (((uint)method.MetadataToken), out func).AssertSucceeded("mod.GetFunctionFromToken (((uint)method.MetadataToken), out func)");;
				ICorDebugValue[] args = new ICorDebugValue[argValues.Length];
				for (int n = 0; n < args.Length; n++)
					args[n] = argValues[n].Val;
				if (methodOwner.Type() == CorElementType.ELEMENT_TYPE_ARRAY || methodOwner.Type() == CorElementType.ELEMENT_TYPE_SZARRAY
					|| MetadataHelperFunctionsExtensions.CoreTypes.ContainsKey (methodOwner.Type())) {
					return ctx.RuntimeInvoke (func, new ICorDebugType[0], target != null ? target.Val : null, args);
				}
				else {
					return ctx.RuntimeInvoke (func, methodOwner.TypeParameters(), target != null ? target.Val : null, args);
				}
			});
			return v.Val == null ? null : v;
		}

		/// <summary>
		/// Returns a pair of method info and a type on which it was resolved
		/// </summary>
		/// <param name="ctx"></param>
		/// <param name="methodName"></param>
		/// <param name="type"></param>
		/// <param name="argtypes"></param>
		/// <param name="flags"></param>
		/// <param name="throwIfNotFound"></param>
		/// <returns></returns>
		Tuple<MethodInfo, ICorDebugType> OverloadResolve (CorEvaluationContext ctx, string methodName, ICorDebugType type, ICorDebugType[] argtypes, BindingFlags flags, bool throwIfNotFound)
		{
			List<Tuple<MethodInfo, ICorDebugType>> candidates = new List<Tuple<MethodInfo, ICorDebugType>> ();
			ICorDebugType currentType = type;

			while (currentType != null) {
				Type rtype = currentType.GetTypeInfo (ctx.Session);
				foreach (MethodInfo met in rtype.GetMethods (flags)) {
					if (met.Name == methodName || (!ctx.CaseSensitive && met.Name.Equals (methodName, StringComparison.CurrentCultureIgnoreCase))) {
						if (argtypes == null)
							return Tuple.Create (met, currentType);
						ParameterInfo[] pars = met.GetParameters ();
						if (pars.Length == argtypes.Length)
							candidates.Add (Tuple.Create (met, currentType));
					}
				}

				if (argtypes == null && candidates.Count > 0)
					break; // when argtypes is null, we are just looking for *any* match (not a specific match)

				if (methodName == ".ctor")
					break; // Can't create objects using constructor from base classes
				if ((rtype.BaseType == null && rtype.FullName != "System.Object") ||
				    currentType.Type() == CorElementType.ELEMENT_TYPE_ARRAY ||
				    currentType.Type() == CorElementType.ELEMENT_TYPE_SZARRAY ||
				    currentType.Type() == CorElementType.ELEMENT_TYPE_STRING) {
					currentType = ctx.Adapter.GetType (ctx, "System.Object") as ICorDebugType;
				} else if (rtype.BaseType != null && rtype.BaseType.FullName == "System.ValueType") {
					currentType = ctx.Adapter.GetType (ctx, "System.ValueType") as ICorDebugType;
				} else {
					// if the currentType is not a class .Base throws an exception ArgumentOutOfRange (thx for coreclr repo for figure it out)
					try {
						ICorDebugType basetype;
						currentType.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
						currentType = basetype;
					}
					catch (Exception) {
						currentType = null;
					}
				}
			}

			return OverloadResolve (ctx, GetTypeName (ctx, type), methodName, argtypes, candidates, throwIfNotFound);
		}

		bool IsApplicable (CorEvaluationContext ctx, MethodInfo method, ICorDebugType[] types, out string error, out int matchCount)
		{
			ParameterInfo[] mparams = method.GetParameters ();
			matchCount = 0;

			for (int i = 0; i < types.Length; i++) {

				Type param_type = mparams[i].ParameterType;

				if (param_type.FullName == GetTypeName (ctx, types[i])) {
					matchCount++;
					continue;
				}

				if (IsAssignableFrom (ctx, param_type, types[i]))
					continue;

				error = String.Format (
					"Argument {0}: Cannot implicitly convert `{1}' to `{2}'",
					i, GetTypeName (ctx, types[i]), param_type.FullName);
				return false;
			}

			error = null;
			return true;
		}

		Tuple<MethodInfo, ICorDebugType> OverloadResolve (CorEvaluationContext ctx, string typeName, string methodName, ICorDebugType[] argtypes, List<Tuple<MethodInfo, ICorDebugType>> candidates, bool throwIfNotFound)
		{
			if (candidates.Count == 1) {
				string error;
				int matchCount;
				if (IsApplicable (ctx, candidates[0].Item1, argtypes, out error, out matchCount))
					return candidates[0];

				if (throwIfNotFound)
					throw new EvaluatorException ("Invalid arguments for method `{0}': {1}", methodName, error);

				return null;
			}

			if (candidates.Count == 0) {
				if (throwIfNotFound)
					throw new EvaluatorException ("Method `{0}' not found in type `{1}'.", methodName, typeName);

				return null;
			}

			// Ok, now we need to find an exact match.
			Tuple<MethodInfo, ICorDebugType> match = null;
			int bestCount = -1;
			bool repeatedBestCount = false;

			foreach (var method in candidates) {
				string error;
				int matchCount;

				if (!IsApplicable (ctx, method.Item1, argtypes, out error, out matchCount))
					continue;

				if (matchCount == bestCount) {
					repeatedBestCount = true;
				}
				else if (matchCount > bestCount) {
					match = method;
					bestCount = matchCount;
					repeatedBestCount = false;
				}
			}

			if (match == null) {
				if (!throwIfNotFound)
					return null;

				if (methodName != null)
					throw new EvaluatorException ("Invalid arguments for method `{0}'.", methodName);

				throw new EvaluatorException ("Invalid arguments for indexer.");
			}

			if (repeatedBestCount) {
				// If there is an ambiguous match, just pick the first match. If the user was expecting
				// something else, he can provide more specific arguments

				/*				if (!throwIfNotFound)
									return null;
								if (methodName != null)
									throw new EvaluatorException ("Ambiguous method `{0}'; need to use full name", methodName);
								else
									throw new EvaluatorException ("Ambiguous arguments for indexer.", methodName);
				*/
			}

			return match;
		}


		public override string[] GetImportedNamespaces (EvaluationContext ctx)
		{
			var list = new HashSet<string> ();
			foreach (Type t in GetAllTypes (ctx)) {
				list.Add (t.Namespace);
			}
			var arr = new string[list.Count];
			list.CopyTo (arr);
			return arr;
		}

		public override void GetNamespaceContents (EvaluationContext ctx, string namspace, out string[] childNamespaces, out string[] childTypes)
		{
			var nss = new HashSet<string> ();
			var types = new HashSet<string> ();
			string namspacePrefix = namspace.Length > 0 ? namspace + "." : "";
			foreach (Type t in GetAllTypes (ctx)) {
				if (t.Namespace == namspace || t.Namespace.StartsWith (namspacePrefix, StringComparison.InvariantCulture)) {
					nss.Add (t.Namespace);
					types.Add (t.FullName);
				}
			}

			childNamespaces = new string[nss.Count];
			nss.CopyTo (childNamespaces);

			childTypes = new string [types.Count];
			types.CopyTo (childTypes);
		}

		bool IsAssignableFrom (CorEvaluationContext ctx, Type baseType, ICorDebugType ctype)
		{
			// the type is method generic parameter, we have to check its constraints, but now we don't have the info about it
			// and assume that any type is assignable to method generic type parameter
			if (baseType is MethodGenericParameter)
				return true;
			string tname = baseType.FullName;
			string ctypeName = GetTypeName (ctx, ctype);
			if (tname == "System.Object")
				return true;

			if (tname == ctypeName)
				return true;

			if (MetadataHelperFunctionsExtensions.CoreTypes.ContainsKey (ctype.Type()))
				return false;

			switch (ctype.Type()) {
				case CorElementType.ELEMENT_TYPE_ARRAY:
				case CorElementType.ELEMENT_TYPE_SZARRAY:
				case CorElementType.ELEMENT_TYPE_BYREF:
				case CorElementType.ELEMENT_TYPE_PTR:
					return false;
			}

			while (ctype != null) {
				if (GetTypeName (ctx, ctype) == tname)
					return true;
				ICorDebugType basetype;
				ctype.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
				ctype = basetype;
			}
			return false;
		}

		public override object TryCast (EvaluationContext ctx, object val, object type)
		{
			var ctype = (ICorDebugType) GetValueType (ctx, val);
            ICorDebugValue obj = GetRealObject(ctx, val);
			var referenceValue = obj as CorApi.ComInterop.ICorDebugReferenceValue;
			if (referenceValue != null)
			{
				int bNull = 0;
				referenceValue.IsNull(&bNull).AssertSucceeded("Could not get if the Reference Value is NULL.");
				if(bNull != 0)
					return val;
			}

			string tname = GetTypeName(ctx, type);
            string ctypeName = GetValueTypeName (ctx, val);
            if (tname == "System.Object")
                return val;

            if (tname == ctypeName)
                return val;

            if (obj is ICorDebugStringValue)
                return ctypeName == tname ? val : null;

            if (obj is ICorDebugArrayValue)
                return (ctypeName == tname || ctypeName == "System.Array") ? val : null;

			var genVal = obj as ICorDebugGenericValue;
			if (genVal != null) {
				Type t = Type.GetType(tname);
				try {
					if (t != null && t.IsPrimitive && t != typeof (string)) {
						object pval = genVal.GetValue ();
						try {
							pval = System.Convert.ChangeType (pval, t);
						}
						catch {
							// pval = DynamicCast (pval, t);
							return null;
						}
						return CreateValue (ctx, pval);
					}
					else if (IsEnum (ctx, (ICorDebugType)type)) {
						return CreateEnum (ctx, (ICorDebugType)type, val);
					}
				} catch {
				}
			}

            if (obj is ICorDebugObjectValue)
            {
				var co = (ICorDebugObjectValue)obj;
				if (IsEnum (ctx, co.GetExactType())) {
					ValueReference rval = GetMember (ctx, null, val, "value__");
					return TryCast (ctx, rval.Value, type);
				}

                while (ctype != null)
                {
                    if (GetTypeName(ctx, ctype) == tname)
                        return val;
	                ICorDebugType basetype;
	                ctype.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
	                ctype = basetype;
                }
                return null;
            }
            return null;
        }

		public bool IsPointer (ICorDebugType targetType)
		{
			return targetType.Type() == CorElementType.ELEMENT_TYPE_PTR;
		}

		public object CreateEnum (EvaluationContext ctx, ICorDebugType type, object val)
		{
			object systemEnumType = GetType (ctx, "System.Enum");
			object enumType = CreateTypeObject (ctx, type);
			object[] argTypes = { GetValueType (ctx, enumType), GetValueType (ctx, val) };
			object[] args = { enumType, val };
			return RuntimeInvoke (ctx, systemEnumType, null, "ToObject", argTypes, args);
		}

		public bool IsEnum (EvaluationContext ctx, ICorDebugType targetType)
		{
			if(targetType.Type() != CorElementType.ELEMENT_TYPE_VALUETYPE && targetType.Type() != CorElementType.ELEMENT_TYPE_CLASS)
				return false;
			ICorDebugType basetype;
			targetType.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
			ICorDebugType targetTypeBase = basetype;
			return targetTypeBase != null && GetTypeName (ctx, targetTypeBase) == "System.Enum";
		}

		public override object CreateValue(EvaluationContext gctx, object value)
		{
			var ctx = (CorEvaluationContext)gctx;
			if(value is string)
				return new CorValRef(() => ctx.Session.NewString(ctx, (string)value));

			foreach(KeyValuePair<CorElementType, Type> tt in MetadataHelperFunctionsExtensions.CoreTypes)
			{
				if(tt.Value != value.GetType())
					continue;
				ICorDebugValue val;
				ctx.Eval.CreateValue(tt.Key, null, out val).AssertSucceeded("ctx.Eval.CreateValue (tt.Key, null, out val)");
				Com.QueryInteface<ICorDebugGenericValue>(val).SetValue(value);
				return new CorValRef(val);
			}
			ctx.WriteDebuggerError(new NotSupportedException(string.Format("Unable to create value for type: {0}", value.GetType())));
			return null;
		}

		public override object CreateValue (EvaluationContext ctx, object type, params object[] gargs)
		{
			CorValRef[] args = CastArray<CorValRef> (gargs);
			return new CorValRef (delegate {
				return CreateCorValue (ctx, (ICorDebugType) type, args);
			});
		}

		public ICorDebugValue CreateCorValue (EvaluationContext ctx, ICorDebugType type, params CorValRef[] args)
		{
			CorEvaluationContext cctx = (CorEvaluationContext)ctx;
			ICorDebugValue[] vargs = new ICorDebugValue [args.Length];
			ICorDebugType[] targs = new ICorDebugType[args.Length];
			for (int n = 0; n < args.Length; n++) {
				vargs [n] = args [n].Val;
				targs [n] = vargs [n].GetExactType();
			}
			MethodInfo ctor = null;
			var ctorInfo = OverloadResolve (cctx, ".ctor", type, targs, BindingFlags.Instance | BindingFlags.Public, false);
			if (ctorInfo != null) {
				ctor = ctorInfo.Item1;
			}
			if (ctor == null) {
				//TODO: Remove this if and content when Generic method invocation is fully implemented
				Type t = type.GetTypeInfo (cctx.Session);
				foreach (MethodInfo met in t.GetMethods ()) {
					if (met.IsSpecialName && met.Name == ".ctor") {
						ParameterInfo[] pinfos = met.GetParameters ();
						if (pinfos.Length == 1) {
							ctor = met;
							break;
						}
					}
				}
			}

			if (ctor == null)
				return null;

			ICorDebugClass typeclass;
			type.GetClass(out typeclass).AssertSucceeded("Could not get the Class of a Type.");
			ICorDebugFunction func;
			ICorDebugModule classmodule;
			typeclass.GetModule(out classmodule).AssertSucceeded("Could not get the Module of a Class.");
			classmodule.GetFunctionFromToken (((uint)ctor.MetadataToken), out func).AssertSucceeded("typeclass.Module.GetFunctionFromToken (((uint)ctor.MetadataToken), out func)");;
			return cctx.RuntimeInvoke (func, type.TypeParameters(), null, vargs);
		}

		public override object CreateNullValue(EvaluationContext gctx, object type)
		{
			ICorDebugValue value;
			Com.QueryInteface<ICorDebugEval2>(((CorEvaluationContext)gctx).Eval).CreateValueForType((ICorDebugType)type, out value).AssertSucceeded("Could not create a NULL value for Type.");
			return new CorValRef(value);
		}

		public override ICollectionAdaptor CreateArrayAdaptor (EvaluationContext ctx, object arr)
		{
			ICorDebugValue val = CorObjectAdaptor.GetRealObject (ctx, arr);
			
			if (val is ICorDebugArrayValue)
				return new ArrayAdaptor (ctx, new CorValRef<ICorDebugArrayValue> ((ICorDebugArrayValue) val, () => (ICorDebugArrayValue) GetRealObject (ctx, arr)));
			return null;
		}
		
		public override IStringAdaptor CreateStringAdaptor (EvaluationContext ctx, object str)
		{
			ICorDebugValue val = CorObjectAdaptor.GetRealObject (ctx, str);
			
			if (val is ICorDebugStringValue)
				return new StringAdaptor (ctx, (CorValRef)str, (ICorDebugStringValue)val);
			return null;
		}

		public static ICorDebugValue GetRealObject (EvaluationContext cctx, object objr)
		{
			if (objr == null)
				return null;

			var corValue = objr as ICorDebugValue;
			if (corValue != null)
				return GetRealObject (cctx, corValue);
			var valRef = objr as CorValRef;
			if (valRef != null)
				return GetRealObject (cctx, valRef.Val);
			return null;
		}

		public static ICorDebugValue GetRealObject (EvaluationContext ctx, ICorDebugValue obj)
		{
			CorEvaluationContext cctx = (CorEvaluationContext) ctx;
			if (obj == null)
				return null;

			try {
				if (obj is ICorDebugStringValue)
					return obj;

				if (obj is ICorDebugGenericValue)
					return obj;

				ICorDebugArrayValue arrayVal = obj as CorApi.ComInterop.ICorDebugArrayValue;
				if (arrayVal != null)
					return arrayVal;

				ICorDebugReferenceValue refVal = obj as CorApi.ComInterop.ICorDebugReferenceValue;
				if (refVal != null) {
					cctx.Session.WaitUntilStopped ();
					int bNull = 0;
					refVal.IsNull(&bNull).AssertSucceeded("Could not get if the Reference Value is NULL.");
					if (bNull != 0)
						return refVal;
					else
					{
						ICorDebugValue deref;
						refVal.Dereference (out deref).AssertSucceeded("refVal.Dereference (out deref)");
						return GetRealObject (cctx, deref);
					}
				}

				cctx.Session.WaitUntilStopped ();
				ICorDebugBoxValue boxVal = obj as ICorDebugBoxValue;
				if (boxVal != null)
					return Unbox (ctx, boxVal);

				if (obj.GetExactType().Type() == CorElementType.ELEMENT_TYPE_STRING)
					return Com.QueryInteface<ICorDebugStringValue>(obj);

				CorElementType eltype=0;
				obj.GetType(&eltype).AssertSucceeded("obj.GetType(&eltype)");
				if (MetadataHelperFunctionsExtensions.CoreTypes.ContainsKey (eltype)) {
					ICorDebugGenericValue genVal = (obj as ICorDebugGenericValue);
					if (genVal != null)
						return genVal;
				}

				if (!(obj is ICorDebugObjectValue))
					return (CorApi.ComInterop.ICorDebugObjectValue)obj;
			}
			catch {
				// Ignore
				throw;
			}
			return obj;
		}

		private static ICorDebugValue Unbox(EvaluationContext ctx, ICorDebugBoxValue boxVal)
		{
			ICorDebugObjectValue bval;
			boxVal.GetObject(out bval).AssertSucceeded("boxVal.GetObject (out bval)");
			Type ptype = Type.GetType(ctx.Adapter.GetTypeName(ctx, bval.GetExactType()));

			if(ptype != null && ptype.IsPrimitive)
			{
				ptype = bval.GetExactType().GetTypeInfo(((CorEvaluationContext)ctx).Session);
				foreach(FieldInfo field in ptype.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if(field.Name != "m_value")
						continue;
					ICorDebugClass typeclass;
					bval.GetExactType().GetClass(out typeclass).AssertSucceeded("Could not get the Class of a Type.");
					ICorDebugValue val;
					bval.GetFieldValue(typeclass, ((uint)field.MetadataToken), out val).AssertSucceeded("bval.GetFieldValue (typeclass, ((uint)field.MetadataToken), out val)");
					val = GetRealObject(ctx, val);
					return val;
				}
			}

			return GetRealObject(ctx, bval);
		}

		public override object GetEnclosingType (EvaluationContext gctx)
		{
			CorEvaluationContext ctx = (CorEvaluationContext) gctx;
			if (ctx.Frame.GetFrameType() != ICorDebugFrameEx.CorFrameType.ILFrame)
				return null;
			ICorDebugFunction framefunction;
			ctx.Frame.GetFunction(out framefunction).AssertSucceeded("Could not get the Function of a Frame.");
			if (framefunction == null)
				return null;

			ICorDebugClass cls;
			framefunction.GetClass(out cls).AssertSucceeded("framefunction.GetClass(out cls)");;
			List<ICorDebugType> tpars = new List<ICorDebugType> ();
			foreach (ICorDebugType t in ctx.Frame.TypeParameters)
				tpars.Add (t);
			return cls.GetParameterizedType (CorElementType.ELEMENT_TYPE_CLASS, tpars.ToArray ());
		}

		public override IEnumerable<EnumMember> GetEnumMembers (EvaluationContext ctx, object tt)
		{
			ICorDebugType t = (ICorDebugType)tt;
			CorEvaluationContext cctx = (CorEvaluationContext)ctx;

			Type type = t.GetTypeInfo (cctx.Session);

			foreach (FieldInfo field in type.GetFields (BindingFlags.Public | BindingFlags.Static)) {
				if (field.IsLiteral && field.IsStatic) {
					object val = field.GetValue (null);
					EnumMember em = new EnumMember ();
					em.Value = (long) System.Convert.ChangeType (val, typeof (long));
					em.Name = field.Name;
					yield return em;
				}
			}
		}

		public override ValueReference GetIndexerReference (EvaluationContext ctx, object target, object[] indices)
		{
			CorEvaluationContext cctx = (CorEvaluationContext)ctx;
			ICorDebugType targetType = GetValueType (ctx, target) as ICorDebugType;

			CorValRef[] values = new CorValRef[indices.Length];
			ICorDebugType[] types = new ICorDebugType[indices.Length];
			for (int n = 0; n < indices.Length; n++) {
				types[n] = (ICorDebugType) GetValueType (ctx, indices[n]);
				values[n] = (CorValRef) indices[n];
			}

			List<Tuple<MethodInfo, ICorDebugType>> candidates = new List<Tuple<MethodInfo, ICorDebugType>> ();
			List<PropertyInfo> props = new List<PropertyInfo> ();
			List<ICorDebugType> propTypes = new List<ICorDebugType> ();

			ICorDebugType t = targetType;
			while (t != null) {
				Type type = t.GetTypeInfo (cctx.Session);

				foreach (PropertyInfo prop in type.GetProperties (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
					MethodInfo mi = null;
					try {
						mi = prop.CanRead ? prop.GetGetMethod (true) : null;
					}
					catch {
						// Ignore
					}
					if (mi != null && !mi.IsStatic && mi.GetParameters ().Length > 0) {
						candidates.Add (Tuple.Create (mi, t));
						props.Add (prop);
						propTypes.Add (t);
					}
				}
				if (cctx.Adapter.IsPrimitive (ctx, target))
					break;
				ICorDebugType basetype;
				t.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
				t = basetype;
			}

			var idx = OverloadResolve (cctx, GetTypeName (ctx, targetType), null, types, candidates, true);
			int i = candidates.IndexOf (idx);

			if (props [i].GetGetMethod (true) == null)
				return null;

			return new PropertyReference (ctx, props[i], (CorValRef)target, propTypes[i], values);
		}

		public override bool HasMember (EvaluationContext ctx, object tt, string memberName, BindingFlags bindingFlags)
		{
			CorEvaluationContext cctx = (CorEvaluationContext) ctx;
			ICorDebugType ct = (ICorDebugType) tt;

			while (ct != null) {
				Type type = ct.GetTypeInfo (cctx.Session);

				FieldInfo field = type.GetField (memberName, bindingFlags);
				if (field != null)
					return true;

				PropertyInfo prop = type.GetProperty (memberName, bindingFlags);
				if (prop != null) {
					MethodInfo getter = prop.CanRead ? prop.GetGetMethod (bindingFlags.HasFlag (BindingFlags.NonPublic)) : null;
					if (getter != null)
						return true;
				}

				if (bindingFlags.HasFlag (BindingFlags.DeclaredOnly))
					break;

				ICorDebugType basetype;
				ct.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
				ct = basetype;
			}

			return false;
		}

		protected override IEnumerable<ValueReference> GetMembers (EvaluationContext ctx, object tt, object gval, BindingFlags bindingFlags)
		{
			var subProps = new Dictionary<string, PropertyInfo> ();
			var t = (ICorDebugType) tt;
			var val = (CorValRef) gval;
			ICorDebugType realType = null;
			if (gval != null && (bindingFlags & BindingFlags.Instance) != 0)
				realType = GetValueType (ctx, gval) as ICorDebugType;

			if (t.Type() == CorElementType.ELEMENT_TYPE_CLASS)
			{
				ICorDebugClass typeclass;
				t.GetClass(out typeclass).AssertSucceeded("Could not get the Class of a Type.");
				if(typeclass == null)
					yield break;
			}

			CorEvaluationContext cctx = (CorEvaluationContext) ctx;

			// First of all, get a list of properties overriden in sub-types
			while (realType != null && realType != t) {
				Type type = realType.GetTypeInfo (cctx.Session);
				foreach (PropertyInfo prop in type.GetProperties (bindingFlags | BindingFlags.DeclaredOnly)) {
					MethodInfo mi = prop.GetGetMethod (true);
					if (mi == null || mi.GetParameters ().Length != 0 || mi.IsAbstract || !mi.IsVirtual || mi.IsStatic)
						continue;
					if (mi.IsPublic && ((bindingFlags & BindingFlags.Public) == 0))
						continue;
					if (!mi.IsPublic && ((bindingFlags & BindingFlags.NonPublic) == 0))
						continue;
					subProps [prop.Name] = prop;
				}
				ICorDebugType basetype;
				realType.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
				realType = basetype;
			}

			while (t != null) {
				Type type = t.GetTypeInfo (cctx.Session);

				foreach (FieldInfo field in type.GetFields (bindingFlags)) {
					if (field.IsStatic && ((bindingFlags & BindingFlags.Static) == 0))
						continue;
					if (!field.IsStatic && ((bindingFlags & BindingFlags.Instance) == 0))
						continue;
					if (field.IsPublic && ((bindingFlags & BindingFlags.Public) == 0))
						continue;
					if (!field.IsPublic && ((bindingFlags & BindingFlags.NonPublic) == 0))
						continue;
					yield return new FieldReference (ctx, val, t, field);
				}

				foreach (PropertyInfo prop in type.GetProperties (bindingFlags)) {
					MethodInfo mi = null;
					try {
						mi = prop.CanRead ? prop.GetGetMethod (true) : null;
					} catch {
						// Ignore
					}
					if (mi == null || mi.GetParameters ().Length != 0 || mi.IsAbstract)
						continue;

					if (mi.IsStatic && ((bindingFlags & BindingFlags.Static) == 0))
						continue;
					if (!mi.IsStatic && ((bindingFlags & BindingFlags.Instance) == 0))
						continue;
					if (mi.IsPublic && ((bindingFlags & BindingFlags.Public) == 0))
						continue;
					if (!mi.IsPublic && ((bindingFlags & BindingFlags.NonPublic) == 0))
						continue;

					// If a property is overriden, return the override instead of the base property
					PropertyInfo overridden;
					if (mi.IsVirtual && subProps.TryGetValue (prop.Name, out overridden)) {
						mi = overridden.GetGetMethod (true);
						if (mi == null)
							continue;

						var declaringType = GetType (ctx, overridden.DeclaringType.FullName) as ICorDebugType;
						yield return new PropertyReference (ctx, overridden, val, declaringType);
					} else {
						yield return new PropertyReference (ctx, prop, val, t);
					}
				}
				if ((bindingFlags & BindingFlags.DeclaredOnly) != 0)
					break;
				ICorDebugType basetype;
				t.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
				t = basetype;
			}
		}

		static T FindByName<T> (IEnumerable<T> elems, Func<T,string> getName, string name, bool caseSensitive)
		{
			T best = default(T);
			foreach (T t in elems) {
				string n = getName (t);
				if (n == name) 
					return t;
				if (!caseSensitive && n.Equals (name, StringComparison.CurrentCultureIgnoreCase))
					best = t;
			}
			return best;
		}

		static bool IsStatic (PropertyInfo prop)
		{
			MethodInfo met = prop.GetGetMethod (true) ?? prop.GetSetMethod (true);
			return met.IsStatic;
		}

		static bool IsAnonymousType (Type type)
		{
			return type.Name.StartsWith ("<>__AnonType", StringComparison.Ordinal);
		}

		static bool IsCompilerGenerated (FieldInfo field)
		{
			return field.GetCustomAttributes (true).Any (v => v is System.Diagnostics.DebuggerHiddenAttribute);
		}

		protected override ValueReference GetMember (EvaluationContext ctx, object t, object co, string name)
		{
			var cctx = ctx as CorEvaluationContext;
			var type = t as ICorDebugType;

			if (IsNullableType (ctx, t)) {
				// 'Value' and 'HasValue' property evaluation gives wrong results when the nullable object is a property of class.
				// Replace to direct field access to fix it. Actual cause of this problem is unknown
				switch (name) {
					case "Value":
						name = "value";
						break;
					case "HasValue":
						name = "hasValue";
						break;
				}
			}

			while (type != null) {
				var tt = type.GetTypeInfo (cctx.Session);
				FieldInfo field = FindByName (tt.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance), f => f.Name, name, ctx.CaseSensitive);
				if (field != null && (field.IsStatic || co != null))
					return new FieldReference (ctx, co as CorValRef, type, field);

				PropertyInfo prop = FindByName (tt.GetProperties (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance), p => p.Name, name, ctx.CaseSensitive);
				if (prop != null && (IsStatic (prop) || co != null)) {
					// Optimization: if the property has a CompilerGenerated backing field, use that instead.
					// This way we avoid overhead of invoking methods on the debugee when the value is requested.
					string cgFieldName = string.Format ("<{0}>{1}", prop.Name, IsAnonymousType (tt) ? "" : "k__BackingField");
					if ((field = FindByName (tt.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance), f => f.Name, cgFieldName, true)) != null && IsCompilerGenerated (field))
						return new FieldReference (ctx, co as CorValRef, type, field, prop.Name, ObjectValueFlags.Property);

					// Backing field not available, so do things the old fashioned way.
					MethodInfo getter = prop.GetGetMethod (true);
					if (getter == null)
						return null;

					return new PropertyReference (ctx, prop, co as CorValRef, type);
				}

				ICorDebugType basetype;
				type.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
				type = basetype;
			}

			return null;
		}

		static bool IsIEnumerable (Type type)
		{
			if (type.Namespace == "System.Collections" && type.Name == "IEnumerable")
				return true;

			if (type.Namespace == "System.Collections.Generic" && type.Name == "IEnumerable`1")
				return true;

			return false;
		}

		static bool IsIEnumerable (ICorDebugType type, CorDebuggerSession session)
		{
			return IsIEnumerable (type.GetTypeInfo (session));
		}

		protected override CompletionData GetMemberCompletionData (EvaluationContext ctx, ValueReference vr)
		{
			var properties = new HashSet<string> ();
			var methods = new HashSet<string> ();
			var fields = new HashSet<string> ();
			var data = new CompletionData ();
			var type = vr.Type as ICorDebugType;
			bool isEnumerable = false;
			Type t;

			var cctx = (CorEvaluationContext)ctx;
			while (type != null) {
				t = type.GetTypeInfo (cctx.Session);
				if (!isEnumerable && IsIEnumerable (t))
					isEnumerable = true;

				foreach (var field in t.GetFields ()) {
					if (field.IsStatic || field.IsSpecialName || !field.IsPublic)
						continue;

					if (fields.Add (field.Name))
						data.Items.Add (new CompletionItem (field.Name, FieldReference.GetFlags (field)));
				}

				foreach (var property in t.GetProperties ()) {
					var getter = property.GetGetMethod (true);

					if (getter == null || getter.IsStatic || !getter.IsPublic)
						continue;

					if (properties.Add (property.Name))
						data.Items.Add (new CompletionItem (property.Name, PropertyReference.GetFlags (property)));
				}

				foreach (var method in t.GetMethods ()) {
					if (method.IsStatic || method.IsConstructor || method.IsSpecialName || !method.IsPublic)
						continue;

					if (methods.Add (method.Name))
						data.Items.Add (new CompletionItem (method.Name, ObjectValueFlags.Method | ObjectValueFlags.Public));
				}

				if (t.BaseType == null && t.FullName != "System.Object")
					type = ctx.Adapter.GetType (ctx, "System.Object") as ICorDebugType;
				else
				{
					ICorDebugType basetype;
					type.GetBase(out basetype).AssertSucceeded("Could not get the Base Type of a Type.");
					type = basetype;
				}
			}

			type = vr.Type as ICorDebugType;
			t = type.GetTypeInfo (cctx.Session);
			foreach (var iface in t.GetInterfaces ()) {
				if (!isEnumerable && IsIEnumerable (iface)) {
					isEnumerable = true;
					break;
				}
			}

			if (isEnumerable) {
				// Look for LINQ extension methods...
				var linq = ctx.Adapter.GetType (ctx, "System.Linq.Enumerable") as ICorDebugType;
				if (linq != null) {
					var linqt = linq.GetTypeInfo (cctx.Session);
					foreach (var method in linqt.GetMethods ()) {
						if (!method.IsStatic || method.IsConstructor || method.IsSpecialName || !method.IsPublic)
							continue;

						if (methods.Add (method.Name))
							data.Items.Add (new CompletionItem (method.Name, ObjectValueFlags.Method | ObjectValueFlags.Public));
					}
				}
			}

			data.ExpressionLength = 0;

			return data;
		}

		public override object TargetObjectToObject (EvaluationContext ctx, object objr)
		{
			ICorDebugValue obj = GetRealObject (ctx, objr);

			if (obj is ICorDebugReferenceValue)
			{
				int bNull = 0;
				((ICorDebugReferenceValue)obj).IsNull(&bNull).AssertSucceeded("Could not get if the Reference Value is NULL.");
				if(bNull != 0)
					return null;
			}

			var stringVal = obj as ICorDebugStringValue;
			if(stringVal != null)
			{
				string str;
				if(ctx.Options.EllipsizeStrings)
				{
					str = LpcwstrHelper.GetString(stringVal.GetString, "Could not get the String Value string.");
					if(str.Length > ctx.Options.EllipsizedLength)
						str = str.Substring(0, ctx.Options.EllipsizedLength) + EvaluationOptions.Ellipsis;
				}
				else
					str = LpcwstrHelper.GetString(stringVal.GetString, "Could not get the String Value string.");
				return str;
			}

			ICorDebugArrayValue arr = obj as ICorDebugArrayValue;
			if (arr != null)
                return base.TargetObjectToObject(ctx, objr);

			ICorDebugObjectValue co = obj as ICorDebugObjectValue;
			if (co != null)
                return base.TargetObjectToObject(ctx, objr);

			ICorDebugGenericValue genVal = obj as ICorDebugGenericValue;
			if (genVal != null)
				return genVal.GetValue ();

			return base.TargetObjectToObject (ctx, objr);
		}

		static bool InGeneratedClosureOrIteratorType (CorEvaluationContext ctx)
		{
			ICorDebugFunction framefunction;
			ctx.Frame.GetFunction(out framefunction).AssertSucceeded("Could not get the Function of a Frame.");
			MethodInfo mi = framefunction.GetMethodInfo (ctx.Session);
			if (mi == null || mi.IsStatic)
				return false;

			Type tm = mi.DeclaringType;
			return IsGeneratedType (tm);
		}

		internal static bool IsGeneratedType (string name)
		{
			//
			// This should cover all C# generated special containers
			// - anonymous methods
			// - lambdas
			// - iterators
			// - async methods
			//
			// which allow stepping into
			//

			return name[0] == '<' &&
				// mcs is of the form <${NAME}>.c__{KIND}${NUMBER}
				(name.IndexOf (">c__", StringComparison.Ordinal) > 0 ||
				// csc is of form <${NAME}>d__${NUMBER}
				name.IndexOf (">d__", StringComparison.Ordinal) > 0);
		}

		internal static bool IsGeneratedType (Type tm)
		{
			return IsGeneratedType (tm.Name);
		}

		private ValueReference GetHoistedThisReference(CorEvaluationContext cx)
		{
			return CorDebugUtil.CallHandlingComExceptions(() =>
			{
				var vref = new CorValRef(delegate
				{
					ICorDebugValue argument;
					Com.QueryInteface<ICorDebugILFrame>(cx.Frame).GetArgument(0, out argument).AssertSucceeded("Com.QueryInteface<ICorDebugILFrame>(cx.Frame).GetArgument (0, out argument)");
					return argument;
				});
				var type = (ICorDebugType)GetValueType(cx, vref);
				return GetHoistedThisReference(cx, type, vref);
			}, "GetHoistedThisReference()");
		}

		ValueReference GetHoistedThisReference (CorEvaluationContext cx, ICorDebugType type, object val)
		{
			Type t = type.GetTypeInfo (cx.Session);
			var vref = (CorValRef)val;
			foreach (FieldInfo field in t.GetFields ()) {
				if (IsHoistedThisReference (field))
					return new FieldReference (cx, vref, type, field, "this", ObjectValueFlags.Literal);

				if (IsClosureReferenceField (field)) {
					var fieldRef = new FieldReference (cx, vref, type, field);
					var fieldType = (ICorDebugType)GetValueType (cx, fieldRef.Value);
					var thisRef = GetHoistedThisReference (cx, fieldType, fieldRef.Value);
					if (thisRef != null)
						return thisRef;
				}
			}

			return null;
		}

		static bool IsHoistedThisReference (FieldInfo field)
		{
			// mcs is "<>f__this" or "$this" (if in an async compiler generated type)
			// csc is "<>4__this"
			return field.Name == "$this" ||
				(field.Name.StartsWith ("<>", StringComparison.Ordinal) &&
				field.Name.EndsWith ("__this", StringComparison.Ordinal));
		}

		static bool IsClosureReferenceField (FieldInfo field)
		{
			// mcs is "<>f__ref"
			// csc is "CS$<>"
			// roslyn is "<>8__"
			return field.Name.StartsWith ("CS$<>", StringComparison.Ordinal) ||
			field.Name.StartsWith ("<>f__ref", StringComparison.Ordinal) ||
			field.Name.StartsWith ("<>8__", StringComparison.Ordinal);
		}

		static bool IsClosureReferenceLocal (ISymbolVariable local)
		{
			if (local.Name == null)
				return false;

			// mcs is "$locvar" or starts with '<'
			// csc is "CS$<>"
			return local.Name.Length == 0 || local.Name[0] == '<' || local.Name.StartsWith ("$locvar", StringComparison.Ordinal) ||
				local.Name.StartsWith ("CS$<>", StringComparison.Ordinal);
		}

		static bool IsGeneratedTemporaryLocal (ISymbolVariable local)
		{
			// csc uses CS$ prefix for temporary variables and <>t__ prefix for async task-related state variables
			return local.Name != null && (local.Name.StartsWith ("CS$", StringComparison.Ordinal) || local.Name.StartsWith ("<>t__", StringComparison.Ordinal));
		}

		protected override ValueReference OnGetThisReference (EvaluationContext ctx)
		{
			CorEvaluationContext cctx = (CorEvaluationContext) ctx;
			if (cctx.Frame.GetFrameType() != ICorDebugFrameEx.CorFrameType.ILFrame)
				return null;
			ICorDebugFunction framefunction;
			cctx.Frame.GetFunction(out framefunction).AssertSucceeded("Could not get the Function of a Frame.");
			if (framefunction == null)
				return null;

			if (InGeneratedClosureOrIteratorType (cctx))
				return GetHoistedThisReference (cctx);

			return GetThisReference (cctx);

		}

		private ValueReference GetThisReference(CorEvaluationContext ctx)
		{
			ICorDebugFunction framefunction;
			ctx.Frame.GetFunction(out framefunction).AssertSucceeded("Could not get the Function of a Frame.");
			MethodInfo mi = framefunction.GetMethodInfo(ctx.Session);
			if(mi == null || mi.IsStatic)
				return null;

			return CorDebugUtil.CallHandlingComExceptions(() =>
			{
				var vref = new CorValRef(delegate
				{
					ICorDebugValue result;
					Com.QueryInteface<ICorDebugILFrame>(ctx.Frame).GetArgument(0, out result).AssertSucceeded("Com.QueryInteface<ICorDebugILFrame>(ctx.Frame).GetArgument (0, out result)");
					CorElementType eltype = 0;
					result.GetType(&eltype).AssertSucceeded("result.GetType(&eltype)");
					if(eltype == CorElementType.ELEMENT_TYPE_BYREF)
						return result.CastToReferenceValue().Dereference();
					return result;
				});

				return new VariableReference(ctx, vref, "this", ObjectValueFlags.Variable | ObjectValueFlags.ReadOnly);
			}, "GetThisReference()");
		}

		private static VariableReference CreateParameterReference(CorEvaluationContext ctx, uint paramIndex, string paramName, ObjectValueFlags flags = ObjectValueFlags.Parameter)
		{
			var vref = new CorValRef(delegate
			{
				ICorDebugValue value;
				Com.QueryInteface<ICorDebugILFrame>(ctx.Frame).GetArgument(paramIndex, out value).AssertSucceeded("Com.QueryInteface<ICorDebugILFrame>(ctx.Frame).GetArgument(paramIndex, out value)");
				return value;
			});
			return new VariableReference(ctx, vref, paramName, flags);
		}


		protected override IEnumerable<ValueReference> OnGetParameters (EvaluationContext gctx)
		{
			CorEvaluationContext ctx = (CorEvaluationContext) gctx;
			if (ctx.Frame.GetFrameType() == ICorDebugFrameEx.CorFrameType.ILFrame)
			{
				ICorDebugFunction framefunction;
				ctx.Frame.GetFunction(out framefunction).AssertSucceeded("Could not get the Function of a Frame.");
				ICorDebugFunction function = framefunction;
				if(function != null)
				{
					MethodInfo met = function.GetMethodInfo (ctx.Session);
					if (met != null) {
						foreach (ParameterInfo pi in met.GetParameters ()) {
							int pos = pi.Position;
							if (met.IsStatic)
								pos--;

							var parameter = CorDebugUtil.CallHandlingComExceptions (() => CreateParameterReference (ctx, ((uint)pos), pi.Name),
								string.Format ("Get parameter {0} of {1}", pi.Name, met.Name));
							if (parameter != null)
								yield return parameter;
						}
						yield break;
					}
				}
			}

			int count = CorDebugUtil.CallHandlingComExceptions (() => ctx.Frame.GetArgumentCount (), "GetArgumentCount()", 0);
			for (uint n = 0; n < count; n++) {
				uint locn = n;
				var parameter = CorDebugUtil.CallHandlingComExceptions (() => CreateParameterReference (ctx, locn, "arg_" + (locn + 1)),
					string.Format ("Get parameter {0}", n));
				if (parameter != null)
					yield return parameter;
			}
		}

		protected override IEnumerable<ValueReference> OnGetLocalVariables (EvaluationContext ctx)
		{
			CorEvaluationContext cctx = (CorEvaluationContext)ctx;
			if (InGeneratedClosureOrIteratorType (cctx)) {
				ValueReference vthis = GetThisReference (cctx);
				return GetHoistedLocalVariables (cctx, vthis).Union (GetLocalVariables (cctx));
			}

			return GetLocalVariables (cctx);
		}

		IEnumerable<ValueReference> GetHoistedLocalVariables (CorEvaluationContext cx, ValueReference vthis)
		{
			if (vthis == null)
				return new ValueReference [0];

			object val = vthis.Value;
			if (IsNull (cx, val))
				return new ValueReference [0];

			ICorDebugType tm = (ICorDebugType) vthis.Type;
			Type t = tm.GetTypeInfo (cx.Session);
			bool isIterator = IsGeneratedType (t);

			var list = new List<ValueReference> ();
			foreach (FieldInfo field in t.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
				if (IsHoistedThisReference (field))
					continue;
				if (IsClosureReferenceField (field)) {
					list.AddRange (GetHoistedLocalVariables (cx, new FieldReference (cx, (CorValRef)val, tm, field)));
					continue;
				}
				if (field.Name[0] == '<') {
					if (isIterator) {
						var name = GetHoistedIteratorLocalName (field);
						if (!string.IsNullOrEmpty (name)) {
							list.Add (new FieldReference (cx, (CorValRef)val, tm, field, name, ObjectValueFlags.Variable));
						}
					}
				} else if (!field.Name.Contains ("$")) {
					list.Add (new FieldReference (cx, (CorValRef)val, tm, field, field.Name, ObjectValueFlags.Variable));
				}
			}
			return list;
		}

		static string GetHoistedIteratorLocalName (FieldInfo field)
		{
			//mcs captured args, of form <$>name
			if (field.Name.StartsWith ("<$>", StringComparison.Ordinal)) {
				return field.Name.Substring (3);
			}

			// csc, mcs locals of form <name>__0
			if (field.Name[0] == '<') {
				int i = field.Name.IndexOf ('>');
				if (i > 1) {
					return field.Name.Substring (1, i - 1);
				}
			}
			return null;
		}

		IEnumerable<ValueReference> GetLocalVariables (CorEvaluationContext cx)
		{
			uint offset;
			CorDebugMappingResult mr;
			return CorDebugUtil.CallHandlingComExceptions (() => {
				cx.Frame.GetIP (out offset, out mr);
				return GetLocals (cx, null, (int) offset, false);
			}, "GetLocalVariables()", new ValueReference[0]);
		}

		public override ValueReference GetCurrentException(EvaluationContext ctx)
		{
			var wctx = (CorEvaluationContext)ctx;
			ICorDebugValue exception;
			wctx.Thread.GetCurrentException(out exception).AssertSucceeded("wctx.Thread.GetCurrentException(out exception)");

			if(exception == null)
				return null;
			return CorDebugUtil.CallHandlingComExceptions(() =>
			{
				var vref = new CorValRef(() => wctx.Session.GetHandle(exception));
				return new VariableReference(ctx, vref, ctx.Options.CurrentExceptionTag, ObjectValueFlags.Variable);
			}, "Get current exception");
		}

		private static VariableReference CreateLocalVariableReference(CorEvaluationContext ctx, uint varIndex, string varName, ObjectValueFlags flags = ObjectValueFlags.Variable)
		{
			return new VariableReference(ctx, new CorValRef(() => ctx.Frame.GetLocalVariable(varIndex)), varName, flags);
		}

		IEnumerable<ValueReference> GetLocals (CorEvaluationContext ctx, ISymbolScope scope, int offset, bool showHidden)
		{
			if (ctx.Frame.GetFrameType() != ICorDebugFrameEx.CorFrameType.ILFrame)
				yield break;

			if (scope == null) {
				ICorDebugFunction framefunction;
				ctx.Frame.GetFunction(out framefunction).AssertSucceeded("Could not get the Function of a Frame.");
				ISymbolMethod met = framefunction.GetSymbolMethod (ctx.Session);
				if (met != null)
					scope = met.RootScope;
				else {
					int count = ctx.Frame.GetLocalVariablesCount ();
					for (uint n = 0; n < count; n++) {
						uint locn = n;
						var localVar = CorDebugUtil.CallHandlingComExceptions (() => CreateLocalVariableReference (ctx, locn, "local_" + (locn + 1)),
							string.Format ("Get local variable {0}", locn));
						if (localVar != null)
							yield return localVar;
					}
					yield break;
				}
			}

			foreach (ISymbolVariable var in scope.GetLocals ()) {
				if (var.Name == "$site")
					continue;
				if (IsClosureReferenceLocal (var)) {
					var variableReference = CorDebugUtil.CallHandlingComExceptions (() => CreateLocalVariableReference (ctx, ((uint)var.AddressField1), var.Name), string.Format ("Get local variable {0}", var.Name));

					if (variableReference != null) {
						foreach (var gv in GetHoistedLocalVariables (ctx, variableReference)) {
							yield return gv;
						}
					}
				} else if (!IsGeneratedTemporaryLocal (var) || showHidden) {
					var variableReference = CorDebugUtil.CallHandlingComExceptions (() => CreateLocalVariableReference (ctx, ((uint)var.AddressField1), var.Name), string.Format ("Get local variable {0}", var.Name));
					if (variableReference != null)
						yield return variableReference;
				}
			}

			foreach (ISymbolScope cs in scope.GetChildren ()) {
				if (cs.StartOffset <= offset && cs.EndOffset >= offset) {
					foreach (ValueReference var in GetLocals (ctx, cs, offset, showHidden))
						yield return var;
				}
			}
		}

		protected override TypeDisplayData OnGetTypeDisplayData (EvaluationContext ctx, object gtype)
		{
			var type = (ICorDebugType) gtype;

			var wctx = (CorEvaluationContext) ctx;
			Type t = type.GetTypeInfo (wctx.Session);
			if (t == null)
				return null;

			string proxyType = null;
			string nameDisplayString = null;
			string typeDisplayString = null;
			string valueDisplayString = null;
			Dictionary<string, DebuggerBrowsableState> memberData = null;
			bool hasTypeData = false;
			bool isCompilerGenerated = false;

			try {
				foreach (object att in t.GetCustomAttributes (false)) {
					DebuggerTypeProxyAttribute patt = att as DebuggerTypeProxyAttribute;
					if (patt != null) {
						proxyType = patt.ProxyTypeName;
						hasTypeData = true;
						continue;
					}
					DebuggerDisplayAttribute datt = att as DebuggerDisplayAttribute;
					if (datt != null) {
						hasTypeData = true;
						nameDisplayString = datt.Name;
						typeDisplayString = datt.Type;
						valueDisplayString = datt.Value;
						continue;
					}
					CompilerGeneratedAttribute cgatt = att as CompilerGeneratedAttribute;
					if (cgatt != null) {
						isCompilerGenerated = true;
						continue;
					}
				}

				ArrayList mems = new ArrayList ();
				mems.AddRange (t.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance));
				mems.AddRange (t.GetProperties (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance));

				foreach (MemberInfo m in mems) {
					object[] atts = m.GetCustomAttributes (typeof (DebuggerBrowsableAttribute), false);
					if (atts.Length > 0) {
						hasTypeData = true;
						if (memberData == null)
							memberData = new Dictionary<string, DebuggerBrowsableState> ();
						memberData[m.Name] = ((DebuggerBrowsableAttribute)atts[0]).State;
					}
				}
			} catch (Exception ex) {
				DebuggerLoggingService.LogError ("Exception in OnGetTypeDisplayData()", ex);
			}
			if (hasTypeData)
				return new TypeDisplayData (proxyType, valueDisplayString, typeDisplayString, nameDisplayString, isCompilerGenerated, memberData);
			else
				return null;
		}

		public override IEnumerable<object> GetNestedTypes(EvaluationContext ctx, object type)
		{
			var cType = (ICorDebugType)type;
			var wctx = (CorEvaluationContext)ctx;
			ICorDebugClass typeclass;
			cType.GetClass(out typeclass).AssertSucceeded("Could not get the Class of a Type.");
			ICorDebugModule mod;
			typeclass.GetModule(out mod).AssertSucceeded("Could not get the Module of a Class.");
			uint mdTypeDef;
			typeclass.GetToken(&mdTypeDef).AssertSucceeded("Could not get the mdTypeDef Token of a Class.");
			uint token = mdTypeDef;
			CorMetadataImport module = wctx.Session.GetMetadataForModule(mod);
			var nesteds = new List<object>();
			foreach(object t in module.DefinedTypes)
			{
				Type declaringType = ((MetadataType)t).DeclaringType;
				if(declaringType == null)
					continue;
				if(declaringType.MetadataToken != token)
					continue;
				ICorDebugClass cls;
				mod.GetClassFromToken(((uint)((MetadataType)t).MetadataToken), out cls).AssertSucceeded("mod.GetClassFromToken (((uint)((MetadataType)t).MetadataToken), out cls)");
				ICorDebugType returnType = cls.GetParameterizedType(CorElementType.ELEMENT_TYPE_CLASS, new ICorDebugType[0]);
				if(!IsGeneratedType(returnType.GetTypeInfo(wctx.Session)))
					nesteds.Add(returnType);
			}
			return nesteds;
		}

		public override IEnumerable<object> GetImplementedInterfaces (EvaluationContext ctx, object type)
		{
			var cType = (ICorDebugType)type;
			var typeInfo = cType.GetTypeInfo (((CorEvaluationContext)ctx).Session);
			foreach (var iface in typeInfo.GetInterfaces ()) {
				if (!string.IsNullOrEmpty (iface.FullName))
					yield return GetType (ctx, iface.FullName);
			}
		}

		// TODO: implement for session
		public override bool IsExternalType (EvaluationContext ctx, object type)
		{
			return base.IsExternalType (ctx, type);
		}

		public override bool IsTypeLoaded (EvaluationContext ctx, string typeName)
		{
			return ctx.Adapter.GetType (ctx, typeName) != null;
		}

		public override bool IsTypeLoaded (EvaluationContext ctx, object type)
		{
			return IsTypeLoaded (ctx, GetTypeName (ctx, type));
		}
		// TODO: Implement GetHoistedLocalVariables
	}
}
