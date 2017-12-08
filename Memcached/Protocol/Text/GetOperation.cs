using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Enyim.Caching.Memcached.Results;
using Enyim.Caching.Memcached.Results.Extensions;

namespace Enyim.Caching.Memcached.Protocol.Text
{
	public class GetOperation : SingleItemOperation, IGetOperation
	{
		private CacheItem result;

		internal GetOperation(string key) : base(key) { }

		protected internal override IList<System.ArraySegment<byte>> GetBuffer()
		{
			var command = "gets " + this.Key + TextSocketHelper.CommandTerminator;

			return TextSocketHelper.GetCommandBuffer(command);
		}

		protected internal override IOperationResult ReadResponse(PooledSocket socket)
		{
			var response = GetHelper.ReadItem(socket);
			var result = new TextOperationResult();

			if (response == null)
				return result.Fail("Failed to read response");

			this.result = response.Item;
			this.Cas = response.CasValue;

			GetHelper.FinishCurrent(socket);

			return result.Pass();
		}

		protected internal override Task<IOperationResult> ReadResponseAsync(PooledSocket socket)
		{
			var tcs = new TaskCompletionSource<IOperationResult>();
			ThreadPool.QueueUserWorkItem(_ =>
			{
				try
				{
					tcs.SetResult(this.ReadResponse(socket));
				}
				catch (Exception ex)
				{
					tcs.SetException(ex);
				}
			});
			return tcs.Task;
		}

		protected internal override bool ReadResponseAsync(PooledSocket socket, System.Action<bool> next)
		{
			throw new NotSupportedException();
		}

		CacheItem IGetOperation.Result
		{
			get { return this.result; }
		}
	}
}

#region [ License information          ]
/* ************************************************************
 * 
 *    � 2010 Attila Kisk� (aka Enyim), � 2016 CNBlogs, � 2018 VIEApps.net
 *    
 *    Licensed under the Apache License, Version 2.0 (the "License");
 *    you may not use this file except in compliance with the License.
 *    You may obtain a copy of the License at
 *    
 *        http://www.apache.org/licenses/LICENSE-2.0
 *    
 *    Unless required by applicable law or agreed to in writing, software
 *    distributed under the License is distributed on an "AS IS" BASIS,
 *    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *    See the License for the specific language governing permissions and
 *    limitations under the License.
 *    
 * ************************************************************/
#endregion
