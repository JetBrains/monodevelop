// 
// AnalysisService.cs
//  
// Author:
//       Michael Hutchinson <mhutchinson@novell.com>
// 
// Copyright (c) 2010 Novell, Inc.
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MonoDevelop.Projects.Dom;
using MonoDevelop.Core;
using System.Threading;

namespace MonoDevelop.AnalysisCore
{
	public static class AnalysisService
	{
		public static IList<Result> Analyze<T> (T input, NodeTreeType treeType)
		{
			Debug.Assert (typeof (T) == AnalysisExtensions.GetType (treeType.Input));
			
			var tree = AnalysisExtensions.GetAnalysisTree (treeType);
			if (tree == null)
				return null;
			
			//force to analyze immediately by evaluating into a list
			return tree.Analyze (input).ToList ();
		}
		
		//TODO: proper job scheduler and discarding superseded jobs
		public static void QueueAnalysis <T> (T input, NodeTreeType treeType, Action<IList<Result>> callback)
		{
			ThreadPool.QueueUserWorkItem (delegate {
				try {
					var results = Analyze (input, treeType);
					callback (results);
				} catch (Exception ex) {
					LoggingService.LogError ("Error in analysis service", ex);
				}
			});
		}
	}
}