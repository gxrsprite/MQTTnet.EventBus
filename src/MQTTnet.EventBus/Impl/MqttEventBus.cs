﻿using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Client;
using MQTTnet.Client.Publishing;
using MQTTnet.Client.Subscribing;
using MQTTnet.Client.Unsubscribing;
using MQTTnet.EventBus.Logger;
using MQTTnet.EventBus.Reflection;
using MQTTnet.Exceptions;
using Polly;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MQTTnet.EventBus.Impl
{
    public class MqttEventBus : IEventBus, IDisposable
    {
        private readonly IEventBusLogger<MqttEventBus> _logger;
        private readonly ISubscriptionsManager _subsManager;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConsumeMethodInvoker _consumeMethodInvoker;
        private readonly IMqttPersisterConnection _mqttPersisterConnection;
        private readonly IEventProvider _eventProvider;
        private readonly int _retryCount;
        private readonly SemaphoreSlim _asyncLocker;

        public MqttEventBus(
            IMqttPersisterConnection mqttPersisterConnection,
            IEventProvider eventProvider,
            IConsumeMethodInvoker consumeMethodInvoker,
            IEventBusLogger<MqttEventBus> logger,
            IServiceScopeFactory scopeFactory,
            ISubscriptionsManager subsManager,
            BusOptions busOptions)
        {
            _mqttPersisterConnection = mqttPersisterConnection ?? throw new ArgumentNullException(nameof(mqttPersisterConnection));
            _mqttPersisterConnection.ClientConnectionChanged += OnConnectionLostAsync;
            _eventProvider = eventProvider;
            _consumeMethodInvoker = consumeMethodInvoker;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _subsManager = subsManager ?? throw new ArgumentNullException(nameof(ISubscriptionsManager));
            _retryCount = busOptions?.RetryCount ?? 5;
            _scopeFactory = scopeFactory;
            _asyncLocker = new SemaphoreSlim(busOptions.MaxConcurrentCalls, busOptions.MaxConcurrentCalls);
        }

        public async Task<MqttClientPublishResult> PublishAsync(MqttApplicationMessage message)
        { 
            var connection = _mqttPersisterConnection;
            if (!connection.IsConnected)
            {
                await connection.TryConnectAsync();
            }

            try
            {
                var policy = Policy
                    .Handle<SocketException>()
                    .Or<MqttCommunicationException>()
                    .Or<Exception>()
                    .WaitAndRetry(_retryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (ex, time) =>
                    {
                        _logger.LogError(ex, $"Could not publish topic: {message?.Topic} after {time.TotalSeconds:n1}s ({ex.Message})");
                    });

                return await policy.Execute(() => connection.GetClient().PublishAsync(message));
            }
            catch { }

            return new MqttClientPublishResult();
        }

        private async Task MessageReceivedAsync(MqttApplicationMessageReceivedEventArgs eventArgs)
        {
            await _asyncLocker.WaitAsync();

            string topic = eventArgs?.ApplicationMessage?.Topic;
            try
            {
                _logger.LogInformation($"Processing Mqtt topic: \"{topic}\"");
                await ProcessEvent(eventArgs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error Processing topic \"{topic}\"");
            }
            finally
            {
                _asyncLocker.Release();
            }
        }

        private async Task ProcessEvent(MqttApplicationMessageReceivedEventArgs eventArgs)
        {
            var message = eventArgs.ApplicationMessage;
            var subscriptions = _subsManager.GetSubscriptions(message.Topic);
            if (subscriptions.IsNullOrEmpty())
            {
                _logger.LogWarning($"No subscription for Mqtt topic: \"{message.Topic}\"");
            }
            else
            {
                foreach (var subscription in subscriptions)
                {
                    using var scope = _scopeFactory.CreateScope();
                    await Task.Yield();
                    await _consumeMethodInvoker.InvokeAsync(scope.ServiceProvider, subscription, eventArgs);
                }
            }
        }

        public void Dispose()
        {
            _mqttPersisterConnection.Dispose();
            _subsManager.Clear();
        }

        public async Task OnConnectionLostAsync(IMqttPersisterConnection connection, MqttClientConnectionEventArgs args)
        {
            if (args.IsReConnected)
            {
                if (args.DisconnectReason == Client.Disconnecting.MqttClientDisconnectReason.NormalDisconnection)
                    await ReSubscribeAllTopicsAsync();
            }
        }

        public async Task<MqttClientSubscribeResult> SubscribeAsync(SubscriptionInfo subscriptionInfo)
        {
            string topic = subscriptionInfo?.Topic;
            _logger.LogInformation($"Subscribing to topic {topic} with {subscriptionInfo?.ConsumerType?.Name}");

            var containsKey = _subsManager.HasSubscriptionsForEvent(topic);
            if (!containsKey)
            {
                if (await _mqttPersisterConnection.TryRegisterMessageHandlerAsync(MessageReceivedAsync))
                {
                    _subsManager.TryAddSubscription(subscriptionInfo);
                    return await OnSubscribesAsync(topic);
                }
            }

            return new MqttClientSubscribeResult();
        }

        private async Task<MqttClientSubscribeResult> OnSubscribesAsync(string topic)
        {
            var connection = _mqttPersisterConnection;
            if (!connection.IsConnected)
            {
                await connection.TryConnectAsync();
            }

            try
            {
                var policy = Policy
                    .Handle<Exception>()
                    .WaitAndRetry(_retryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (ex, time) =>
                    {
                        _logger.LogError(ex, $"Could not Subscribe topic: {topic} after {time.TotalSeconds:n1}s ({ex.Message})");
                    });

                return await policy.Execute(() => connection.GetClient().SubscribeAsync(topic));
            }
            catch { }

            return new MqttClientSubscribeResult();
        }

        public async Task<MqttClientUnsubscribeResult> UnsubscribeAsync(SubscriptionInfo subscriptionInfo)
        {
            var connection = _mqttPersisterConnection;
            if (!connection.IsConnected)
            {
                await connection.TryConnectAsync();
            }

            var topic = subscriptionInfo?.Topic; 
            _logger.LogInformation($"Unsubscribing to topic {topic} with {subscriptionInfo?.ConsumerType?.Name}");

            var containsKey = _subsManager.HasSubscriptionsForEvent(topic);
            if (!containsKey)
            {
                _subsManager.RemoveSubscription(subscriptionInfo);
                return await connection.RemoveSubscriptionAsync(topic);
            }

            return new MqttClientUnsubscribeResult();
        }

        public Task<MqttClientSubscribeResult[]> ReSubscribeAllTopicsAsync()
        {
            var subscribers = _subsManager.AllTopics().Select(async topic =>
            {
                var connection = _mqttPersisterConnection;
                if (connection.IsConnected || await connection.TryConnectAsync())
                    return await OnSubscribesAsync(topic);
                return new MqttClientSubscribeResult();
            });

            return Task.WhenAll(subscribers);
        }
    }
}