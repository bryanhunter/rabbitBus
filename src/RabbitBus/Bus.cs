﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using RabbitBus.Configuration;
using RabbitBus.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RabbitBus
{
	public class Bus : IBus, IDisposable
	{
		readonly IConfigurationModel _configurationModel;
		readonly Action<IErrorContext> _defaultErrorCallback;
		readonly IDictionary<ISubscriptionKey, ISubscription> _subscriptions;
		object _connectionLock = new object();
		bool _closed;
		IConnection _connection;
		ConnectionFactory _connectionFactory;
		bool _disposed;
		IMessagePublisher _messagePublisher;

		public Bus(IConfigurationModel configurationModel)
		{
			_configurationModel = configurationModel;
			_defaultErrorCallback = OnConsumeError;
			_subscriptions = new Dictionary<ISubscriptionKey, ISubscription>();
		}

		public void Publish<TMessage>(TMessage message)
		{
			PublishMessage(message, null, null);
		}

		public void Publish<TMessage>(TMessage message, IDictionary headers)
		{
			PublishMessage(message, null, headers);
		}

		public void Publish<TRequestMessage, TReplyMessage>(TRequestMessage requestMessage,
		                                                    Action<IMessageContext<TReplyMessage>> action)
		{
			PublishMessage(requestMessage, null, null, action);
		}

		public void Publish<TMessage>(TMessage message, string routingKey)
		{
			PublishMessage(message, routingKey, null);
		}

		public void Unsubscribe<TMessage>()
		{
			UnsubscribeMessage<TMessage>(null, null);
		}

		public void Unsubscribe<TMessage>(string routingKey)
		{
			UnsubscribeMessage<TMessage>(routingKey, null);
		}

		public void Unsubscribe<TMessage>(IDictionary headers)
		{
			UnsubscribeMessage<TMessage>(null, headers);
		}

		public void Subscribe<TMessage>(Action<IMessageContext<TMessage>> action)
		{
			SubscribeMessage(action, null, null);
		}

		public void Subscribe<TMessage>(Action<IMessageContext<TMessage>> action, IDictionary headers)
		{
			SubscribeMessage(action, null, headers);
		}

		public void Subscribe<TMessage>(Action<IMessageContext<TMessage>> action, string routingKey)
		{
			SubscribeMessage(action, routingKey, null);
		}

		public IConsumerContext<TMessage> CreateConsumerContext<TMessage>()
		{
			Logger.Current.Write(new LogEntry {Message = "Creating ConsumerContext ...", Severity = TraceEventType.Information});
			return new ConsumerContext<TMessage>(_connection,
			                                     _configurationModel.ConsumeRouteConfiguration.GetRouteInfo(typeof (TMessage)),
			                                     _configurationModel.DefaultSerializationStrategy,
			                                     _configurationModel.DefaultDeadLetterStrategy, _messagePublisher);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		void RegisterAutoSubscriptions(IConfigurationModel configurationModel)
		{
			foreach (AutoSubscription autoSubscription in configurationModel.AutoSubscriptions)
			{
				object messageHandler = Activator.CreateInstance(autoSubscription.MessageHandlerType);
				Type messageContext = typeof (IMessageContext<>).MakeGenericType(autoSubscription.MessageType);
				Type action = typeof (Action<>).MakeGenericType(messageContext);
				Delegate handler = Delegate.CreateDelegate(action, messageHandler, "Handle");
				MethodInfo openSubscribeMessage = typeof (Bus).GetMethod("SubscribeMessage",
				                                                         BindingFlags.Instance | BindingFlags.NonPublic);
				MethodInfo closedSubscribedMessage = openSubscribeMessage.MakeGenericMethod(new[] {autoSubscription.MessageType});
				closedSubscribedMessage.Invoke(this, new[] {handler, null, null});
			}
		}

		void UnsubscribeMessage<TMessage>(string routingKey, IDictionary headers)
		{
			ISubscription subscription;
			var key = new SubscriptionKey(typeof (TMessage), routingKey, headers);
			_subscriptions.TryGetValue(key, out subscription);

			if (subscription != null)
			{
				subscription.Stop();
				_subscriptions.Remove(key);
			}
		}

		void PublishMessage<TRequestMessage, TReplyMessage>(TRequestMessage message, string routingKey, IDictionary headers,
		                                                    Action<IMessageContext<TReplyMessage>> replyAction)
		{
			_messagePublisher.Publish(message, routingKey, headers, replyAction);
		}

		void PublishMessage<TMessage>(TMessage message, string routingKey, IDictionary headers)
		{
			_messagePublisher.Publish(message, routingKey, headers);
		}

		public void Connect()
		{
			Connect("amqp://guest:guest@localhost:5672/%2f");
		}

		public void Connect(string ampqUri)
		{
			_connectionFactory = new ConnectionFactory
			                     	{
			                     		Uri = ampqUri
			                     	};

			_messagePublisher = new MessagePublisher(_connectionFactory.UserName,
			                                         _configurationModel.PublicationRouteConfiguration,
			                                         _configurationModel.ConsumeRouteConfiguration,
			                                         _configurationModel.DefaultSerializationStrategy,
			                                         _configurationModel.ConnectionDownQueueStrategy);
			InitializeConnection(_connectionFactory);
			RegisterAutoSubscriptions(_configurationModel);
		}

		void InitializeConnection(ConnectionFactory connectionFactory)
		{
				Logger.Current.Write("Initializing connection ...", TraceEventType.Information);
				_connection = connectionFactory.CreateConnection();
				_connection.ConnectionShutdown += UnexpectedConnectionShutdown;
				_connection.CallbackException += _connection_CallbackException;
				_messagePublisher.SetConnection(_connection);
				_configurationModel.DefaultDeadLetterStrategy.SetConnection(_connection);

				Logger.Current.Write(new LogEntry
				                     	{
				                     		Message = string.Format("Connected to the RabbitMQ node on host:{0}, port:{1}.",
				                     		                        _connection.Endpoint.HostName, _connection.Endpoint.Port)
				                     	});

				OnConnectionEstablished(EventArgs.Empty);
		}

		void UnexpectedConnectionShutdown(IConnection connection, ShutdownEventArgs reason)
		{
			Logger.Current.Write("Connection was shut down.", TraceEventType.Information);
			_connection.ConnectionShutdown -= UnexpectedConnectionShutdown;

			lock(_connectionLock)
			{
				if (_closed) return;
				Reconnect(TimeSpan.FromSeconds(10));
				RenewSubscriptions(_subscriptions.Values);
				_messagePublisher.Flush();
			}
		}

		void _connection_CallbackException(object sender, CallbackExceptionEventArgs e)
		{
			Logger.Current.Write("CallbackException received: " + e.Exception.Message, TraceEventType.Information);
		}

		void RenewSubscriptions(IEnumerable<ISubscription> subscriptions)
		{
			Logger.Current.Write("Renewing subscriptions ...", TraceEventType.Information);

			foreach (ISubscription subscription in subscriptions)
			{
				subscription.Renew(_connection);
			}
			Logger.Current.Write("Subscriptions have been renewed.", TraceEventType.Information);
		}

		void Reconnect(TimeSpan timeSpan)
		{
			try
			{
				Logger.Current.Write(string.Format("Attempting reconnect with last known configuration in {0} seconds.",
				                                   timeSpan.ToString("ss")), TraceEventType.Information);
				TimeProvider.Current.Sleep(_configurationModel.ReconnectionInterval);
				InitializeConnection(_connectionFactory);
			}
			catch (Exception e)
			{
				Logger.Current.Write("Connection failed.", TraceEventType.Information);
			}
		}

		public void Close()
		{
			lock(_connectionLock)
			{
				_connection.ConnectionShutdown -= UnexpectedConnectionShutdown;
				if (_connection != null && _connection.IsOpen)
				{
					_connection.Close();
					string message = string.Format("Disconnected from the RabbitMQ node on host:{0}, port:{1}.",
					                               _connection.Endpoint.HostName, _connection.Endpoint.Port);
					Logger.Current.Write(new LogEntry {Message = message});
				}
				_closed = true;
			}
		}

		void SubscribeMessage<TMessage>(Action<IMessageContext<TMessage>> action, string routingKey, IDictionary arguments)
		{
			IConsumeInfo routeInfo = _configurationModel.ConsumeRouteConfiguration.GetRouteInfo(typeof (TMessage));
			var subscription = new Subscription<TMessage>(_connection, _configurationModel.DefaultDeadLetterStrategy,
			                                              _configurationModel.DefaultSerializationStrategy,
			                                              routeInfo, routingKey, action, arguments, _defaultErrorCallback,
			                                              _messagePublisher, SubscriptionType.Subscription);
			_subscriptions.Add(new SubscriptionKey(typeof (TMessage), routingKey, arguments), subscription);
			subscription.Start();
		}

		static void OnConsumeError(IErrorContext errorContext)
		{
			errorContext.RejectMessage(false);
		}

		public event EventHandler ConnectionEstablished;

		public void OnConnectionEstablished(EventArgs e)
		{
			EventHandler handler = ConnectionEstablished;
			if (handler != null) handler(this, e);
		}

		~Bus()
		{
			Dispose(false);
		}

		void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					// free managed
				}
				Close();
				_disposed = true;
			}
		}
	}
}