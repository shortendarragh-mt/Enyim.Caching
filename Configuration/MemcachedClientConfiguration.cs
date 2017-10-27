using System;
using System.Net;
using System.Collections.Generic;
using System.Configuration;
using System.Xml;

using Newtonsoft.Json.Linq;

using Enyim.Reflection;
using Enyim.Caching.Memcached;
using Enyim.Caching.Memcached.Protocol.Binary;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enyim.Caching.Configuration
{
	/// <summary>
	/// Configuration class
	/// </summary>
	public class MemcachedClientConfiguration : IMemcachedClientConfiguration
	{
		// these are lazy initialized in the getters
		private Type nodeLocator;
		private ITranscoder _transcoder;
		private IMemcachedKeyTransformer keyTransformer;
		private ILogger<MemcachedClientConfiguration> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="T:MemcachedClientConfiguration"/> class.
		/// </summary>
		public MemcachedClientConfiguration(ILoggerFactory loggerFactory, IOptions<MemcachedClientOptions> optionsAccessor)
		{
			if (optionsAccessor == null)
				throw new ArgumentNullException(nameof(optionsAccessor));

			this._logger = loggerFactory.CreateLogger<MemcachedClientConfiguration>();

			var options = optionsAccessor.Value;
			this.Servers = new List<EndPoint>();
			foreach (var server in options.Servers)
			{
				if (IPAddress.TryParse(server.Address, out IPAddress address))
					Servers.Add(new IPEndPoint(address, server.Port));
				else
					Servers.Add(new DnsEndPoint(server.Address, server.Port));
			}
			this.SocketPool = options.SocketPool;
			this.Protocol = options.Protocol;

			if (options.Authentication != null && !string.IsNullOrEmpty(options.Authentication.Type))
			{
				try
				{
					var authenticationType = Type.GetType(options.Authentication.Type);
					if (authenticationType != null)
					{
						this._logger.LogDebug($"Authentication type is {authenticationType}.");

						this.Authentication = new AuthenticationConfiguration();
						this.Authentication.Type = authenticationType;
						foreach (var parameter in options.Authentication.Parameters)
						{
							this.Authentication.Parameters[parameter.Key] = parameter.Value;
							_logger.LogDebug($"Authentication {parameter.Key} is '{parameter.Value}'.");
						}
					}
					else
					{
						_logger.LogError($"Unable to load authentication type {options.Authentication.Type}.");
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(new EventId(), ex, $"Unable to load authentication type {options.Authentication.Type}.");
				}
			}

			if (!string.IsNullOrEmpty(options.KeyTransformer))
			{
				try
				{
					var keyTransformerType = Type.GetType(options.KeyTransformer);
					if (keyTransformerType != null)
					{
						this.KeyTransformer = Activator.CreateInstance(keyTransformerType) as IMemcachedKeyTransformer;
						_logger.LogDebug($"Use '{options.KeyTransformer}' KeyTransformer");
					}
				}
				catch (Exception ex)
				{
					this._logger.LogError(new EventId(), ex, $"Unable to load '{options.KeyTransformer}' KeyTransformer");
				}
			}

			if (options.Transcoder != null)
			{
				this._transcoder = options.Transcoder;
			}

			if (options.NodeLocatorFactory != null)
			{
				this.NodeLocatorFactory = options.NodeLocatorFactory;
			}
		}

		public MemcachedClientConfiguration(ILoggerFactory loggerFactory, MemcachedClientConfigurationSectionHandler configuration)
		{
			this._logger = loggerFactory.CreateLogger<MemcachedClientConfiguration>();
			this.Protocol = MemcachedProtocol.Binary;

			this.Servers = new List<EndPoint>();
			if (configuration.Section.SelectNodes("servers/add") is XmlNodeList nodes)
				foreach (XmlNode server in nodes)
				{
					var info = configuration.GetJson(server);
					this.AddServer((info["address"] as JValue).Value as string, Convert.ToInt32((info["port"] as JValue).Value));
				}

			this.SocketPool = new SocketPoolConfiguration();
			if (configuration.Section.SelectSingleNode("socketPool") is XmlNode socketpool)
			{
				var info = configuration.GetJson(socketpool);
				if (info["maxPoolSize"] != null)
					this.SocketPool.MaxPoolSize = Convert.ToInt32((info["maxPoolSize"] as JValue).Value);
				if (info["minPoolSize"] != null)
					this.SocketPool.MinPoolSize = Convert.ToInt32((info["minPoolSize"] as JValue).Value);
				if (info["connectionTimeout"] != null)
					this.SocketPool.ConnectionTimeout = TimeSpan.Parse((info["connectionTimeout"] as JValue).Value as string);
				if (info["deadTimeout"] != null)
					this.SocketPool.DeadTimeout = TimeSpan.Parse((info["deadTimeout"] as JValue).Value as string);
				if (info["queueTimeout"] != null)
					this.SocketPool.QueueTimeout = TimeSpan.Parse((info["queueTimeout"] as JValue).Value as string);
				if (info["receiveTimeout"] != null)
					this.SocketPool.ReceiveTimeout = TimeSpan.Parse((info["receiveTimeout"] as JValue).Value as string);

				var failurePolicy = info["failurePolicy"] != null ? (info["failurePolicy"] as JValue).Value as string : null;
				if ("throttling" == failurePolicy)
				{
					var failureThreshold = info["failureThreshold"] != null ? Convert.ToInt32((info["failureThreshold"] as JValue).Value) : 4;
					var resetAfter = info["resetAfter"] != null ? TimeSpan.Parse((info["resetAfter"] as JValue).Value as string) : TimeSpan.FromSeconds(300);
					this.SocketPool.FailurePolicyFactory = new ThrottlingFailurePolicyFactory(failureThreshold, resetAfter);
				}
			}

			if (configuration.Section.SelectSingleNode("authentication") is XmlNode authentication)
			{
				var info = configuration.GetJson(authentication);
				if (info["type"] != null)
					try
					{
						var authenticationType = Type.GetType((info["type"] as JValue).Value as string);
						if (authenticationType != null)
						{
							this._logger.LogDebug($"Authentication type is {authenticationType}.");

							this.Authentication = new AuthenticationConfiguration();
							this.Authentication.Type = authenticationType;
							if (info["zone"] != null)
								this.Authentication.Parameters.Add("zone", (info["zone"] as JValue).Value);
							if (info["userName"] != null)
								this.Authentication.Parameters.Add("userName", (info["userName"] as JValue).Value);
							if (info["password"] != null)
								this.Authentication.Parameters.Add("password", (info["password"] as JValue).Value);
						}
						else
						{
							_logger.LogError($"Unable to load authentication type {(info["type"] as JValue).Value as string}.");
						}
					}
					catch (Exception ex)
					{
						_logger.LogError(new EventId(), ex, $"Unable to load authentication type {(info["type"] as JValue).Value as string}.");
					}
			}
		}

		/// <summary>
		/// Adds a new server to the pool.
		/// </summary>
		/// <param name="address">The address and the port of the server in the format 'host:port'.</param>
		public void AddServer(string address)
		{
			this.Servers.Add(ConfigurationHelper.ResolveToEndPoint(address));
		}

		/// <summary>
		/// Adds a new server to the pool.
		/// </summary>
		/// <param name="address">The host name or IP address of the server.</param>
		/// <param name="port">The port number of the memcached instance.</param>
		public void AddServer(string address, int port)
		{
			this.Servers.Add(ConfigurationHelper.ResolveToEndPoint(address, port));
		}

		/// <summary>
		/// Gets a list of <see cref="T:IPEndPoint"/> each representing a Memcached server in the pool.
		/// </summary>
		public IList<EndPoint> Servers { get; private set; }

		/// <summary>
		/// Gets the configuration of the socket pool.
		/// </summary>
		public ISocketPoolConfiguration SocketPool { get; private set; }

		/// <summary>
		/// Gets the authentication settings.
		/// </summary>
		public IAuthenticationConfiguration Authentication { get; private set; }

		/// <summary>
		/// Gets or sets the <see cref="T:Enyim.Caching.Memcached.IMemcachedKeyTransformer"/> which will be used to convert item keys for Memcached.
		/// </summary>
		public IMemcachedKeyTransformer KeyTransformer
		{
			get { return this.keyTransformer ?? (this.keyTransformer = new DefaultKeyTransformer()); }
			set { this.keyTransformer = value; }
		}

		/// <summary>
		/// Gets or sets the Type of the <see cref="T:Enyim.Caching.Memcached.IMemcachedNodeLocator"/> which will be used to assign items to Memcached nodes.
		/// </summary>
		/// <remarks>If both <see cref="M:NodeLocator"/> and  <see cref="M:NodeLocatorFactory"/> are assigned then the latter takes precedence.</remarks>
		public Type NodeLocator
		{
			get { return this.nodeLocator; }
			set
			{
				ConfigurationHelper.CheckForInterface(value, typeof(IMemcachedNodeLocator));
				this.nodeLocator = value;
			}
		}

		/// <summary>
		/// Gets or sets the NodeLocatorFactory instance which will be used to create a new IMemcachedNodeLocator instances.
		/// </summary>
		/// <remarks>If both <see cref="M:NodeLocator"/> and  <see cref="M:NodeLocatorFactory"/> are assigned then the latter takes precedence.</remarks>
		public IProviderFactory<IMemcachedNodeLocator> NodeLocatorFactory { get; set; }

		/// <summary>
		/// Gets or sets the <see cref="T:Enyim.Caching.Memcached.ITranscoder"/> which will be used serialize or deserialize items.
		/// </summary>
		public ITranscoder Transcoder
		{
			get { return _transcoder ?? (_transcoder = new DefaultTranscoder()); }
			set { _transcoder = value; }
		}

		/// <summary>
		/// Gets or sets the type of the communication between client and server.
		/// </summary>
		public MemcachedProtocol Protocol { get; set; }

		#region [ interface                     ]

		IList<System.Net.EndPoint> IMemcachedClientConfiguration.Servers
		{
			get { return this.Servers; }
		}

		ISocketPoolConfiguration IMemcachedClientConfiguration.SocketPool
		{
			get { return this.SocketPool; }
		}

		IAuthenticationConfiguration IMemcachedClientConfiguration.Authentication
		{
			get { return this.Authentication; }
		}

		IMemcachedKeyTransformer IMemcachedClientConfiguration.CreateKeyTransformer()
		{
			return this.KeyTransformer;
		}

		IMemcachedNodeLocator IMemcachedClientConfiguration.CreateNodeLocator()
		{
			var f = this.NodeLocatorFactory;
			if (f != null) return f.Create();

			return this.NodeLocator == null
					? new SingleNodeLocator()
				: (IMemcachedNodeLocator)FastActivator.Create(this.NodeLocator);
		}

		ITranscoder IMemcachedClientConfiguration.CreateTranscoder()
		{
			return this.Transcoder;
		}

		IServerPool IMemcachedClientConfiguration.CreatePool()
		{
			switch (this.Protocol)
			{
				case MemcachedProtocol.Text:
					return new DefaultServerPool(this, new Memcached.Protocol.Text.TextOperationFactory(), _logger);

				case MemcachedProtocol.Binary:
					return new BinaryPool(this, _logger);
			}

			throw new ArgumentOutOfRangeException("Unknown protocol: " + (int)this.Protocol);
		}

		#endregion

	}

	public class MemcachedClientConfigurationSectionHandler : IConfigurationSectionHandler
	{
		public object Create(object parent, object configContext, XmlNode section)
		{
			this._section = section;
			return this;
		}

		XmlNode _section = null;

		public XmlNode Section { get { return this._section; } }

		public JObject GetJson(XmlNode node)
		{
			var settings = new JObject();
			foreach (XmlAttribute attribute in node.Attributes)
				settings.Add(new JProperty(attribute.Name, attribute.Value));
			return settings;
		}
	}
}

#region [ License information          ]
/* ************************************************************
 * 
 *    Copyright (c) 2010 Attila Kisk? enyim.com
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
