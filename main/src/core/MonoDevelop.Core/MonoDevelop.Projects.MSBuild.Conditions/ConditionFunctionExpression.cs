//
// ConditionFunctionExpression.cs
//
// Authors:
//   Marek Sieradzki (marek.sieradzki@gmail.com)
//   Marek Safar (marek.safar@gmail.com)
// 
// (C) 2006 Marek Sieradzki
// Copyright (c) 2014 Xamarin Inc. (http://www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;

namespace MonoDevelop.Projects.MSBuild.Conditions {
	internal sealed class ConditionFunctionExpression : ConditionExpression {
	
		List <ConditionFactorExpression> 	args;
		string					name;
		
		static readonly Dictionary<string, Func<string, IExpressionContext, bool>> functions = new Dictionary<string, Func<string, IExpressionContext, bool>> (StringComparer.OrdinalIgnoreCase) {
			{ "Exists", Exists },
			{ "HasTrailingSlash" , HasTrailingSlash }
		};
		
		public ConditionFunctionExpression (string name, List <ConditionFactorExpression> args)
		{
			this.args = args;
			this.name = name;
		}
		
		public override  bool BoolEvaluate (IExpressionContext context)
		{
			Func<string, IExpressionContext, bool> func;
			if (!functions.TryGetValue (name, out func)) {
				// MSB4091
				throw new Exception (string.Format ("Found a call to an undefined function \"{0}\".", name));
			}

			if (args.Count != 1) {
				// MSB4089
				throw new Exception (string.Format ("Incorrect number of arguments to function in condition \"{0}\". Found {1} argument(s) when expecting {2}.",
					name, args.Count, 1));
			}
			
			return func (args [0].StringEvaluate (context), context);
		}
		
		public override float NumberEvaluate (IExpressionContext context)
		{
			throw new NotSupportedException ();
		}
		
		public override string StringEvaluate (IExpressionContext context)
		{
			throw new NotSupportedException ();
		}
		
		public override bool CanEvaluateToBool (IExpressionContext context)
		{
			return functions.ContainsKey (name);
		}
		
		public override bool CanEvaluateToNumber (IExpressionContext context)
		{
			return false;
		}
		
		public override bool CanEvaluateToString (IExpressionContext context)
		{
			return false;
		}

#pragma warning disable 0169
#region Functions
		// FIXME imported projects
		static bool Exists (string file, IExpressionContext context)
		{
			if (string.IsNullOrEmpty (file))
				return false;

			string directory  = null;
			
			if (context.FullFileName != String.Empty)
				directory = Path.GetDirectoryName (context.FullFileName);

			file = MSBuildProjectService.FromMSBuildPath (directory, file);
			string res;
			if (context.EvaluationCache.TryGetValue (file, out res))
				return bool.Parse (res);
			
			var ret =  File.Exists (file) || Directory.Exists (file);
			context.EvaluationCache [file] = ret.ToString ();
			return ret;
		}

		static bool HasTrailingSlash (string file, IExpressionContext context)
		{
			if (file == null)
				return false;

			file = file.Trim ();

			int len = file.Length;
			if (len == 0)
				return false;

			return file [len - 1] == '\\' || file [len - 1] == '/';
		}

#endregion
#pragma warning restore 0169

	}
}
