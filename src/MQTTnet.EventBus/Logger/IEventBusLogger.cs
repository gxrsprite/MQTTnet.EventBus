using Microsoft.Extensions.Logging;
using MQTTnet.Diagnostics;
using System;
using System.Collections.Generic;

namespace MQTTnet.EventBus.Logger
{
    public interface IMqttNetLogger
    {
        IMqttNetScopedLogger CreateScopedLogger(string source);

        void Publish(MqttNetLogLevel logLevel, string source, string message, object[] parameters, Exception exception);
    }

    public interface IMqttNetScopedLogger
    {
        IMqttNetScopedLogger CreateScopedLogger(string source);

        void Publish(MqttNetLogLevel logLevel, string message, object[] parameters, Exception exception);
    }

    public interface IEventBusLogger : IMqttNetScopedLogger
    {
        IEventBusLogger CreateLogger(string categoryName);
        IEventBusLogger<TCategoryName> CreateLogger<TCategoryName>();

        void LogTrace(string message);
        void LogInformation(string message);
        void LogWarning(string message);
        void LogWarning(Exception ex, string message);
        void LogError(Exception ex, string message);
    }

    public class MicrosoftEventBusLogger : IEventBusLogger
    {
        protected readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;
        private static readonly IDictionary<MqttNetLogLevel, LogLevel> _logLevelMap;

        static MicrosoftEventBusLogger()
        {
            _logLevelMap = new Dictionary<MqttNetLogLevel, LogLevel>
            {
                { MqttNetLogLevel.Verbose, LogLevel.Trace },
                { MqttNetLogLevel.Info, LogLevel.Information },
                { MqttNetLogLevel.Warning, LogLevel.Warning },
                { MqttNetLogLevel.Error, LogLevel.Error }
            };
        }

        public MicrosoftEventBusLogger(ILoggerFactory loggerFactory, ILogger logger)
        {
            _loggerFactory = loggerFactory;
            _logger = logger;
        }

        public IEventBusLogger CreateLogger(string categoryName)
            => new MicrosoftEventBusLogger(_loggerFactory, _loggerFactory.CreateLogger(categoryName));

        public IMqttNetScopedLogger CreateScopedLogger(string source)
            => new MicrosoftEventBusLogger(_loggerFactory, _loggerFactory.CreateLogger(source));

        public IEventBusLogger<TCategoryName> CreateLogger<TCategoryName>()
            => new MicrosoftEventBusLogger<TCategoryName>(_loggerFactory, _loggerFactory.CreateLogger<TCategoryName>());

        public void LogTrace(string message)
            => _logger.LogTrace(message);

        public void LogInformation(string message)
            => _logger.LogInformation(message);

        public void LogWarning(string message)
            => _logger.LogWarning(message);

        public void LogWarning(Exception ex, string message)
            => _logger.LogWarning(ex, message);

        public void LogError(Exception ex, string message)
            => _logger.LogError(ex, message);

        public void Publish(MqttNetLogLevel logLevel, string message, object[] parameters, Exception exception)
            => _logger.Log(_logLevelMap[logLevel], exception, message, parameters);
    }

    public class MicrosoftEventBusLogger<TCategoryName> : MicrosoftEventBusLogger, IEventBusLogger<TCategoryName>
    {
        public MicrosoftEventBusLogger(ILoggerFactory loggerFactory, ILogger<TCategoryName> logger)
            : base(loggerFactory, logger)
        { }
    }

    public static class MqttNetScopedLoggerExtensions
    {
        public static void Verbose(this IMqttNetScopedLogger logger, string message, params object[] parameters)
        {
            logger.Publish(MqttNetLogLevel.Verbose, message, parameters, null);
        }

        public static void Info(this IMqttNetScopedLogger logger, string message, params object[] parameters)
        {
            logger.Publish(MqttNetLogLevel.Info, message, parameters, null);
        }

        public static void Warning(this IMqttNetScopedLogger logger, Exception exception, string message, params object[] parameters)
        {
            logger.Publish(MqttNetLogLevel.Warning, message, parameters, exception);
        }

        public static void Warning(this IMqttNetScopedLogger logger, string message, params object[] parameters)
        {
            logger.Publish(MqttNetLogLevel.Warning, message, parameters, null);
        }

        public static void Error(this IMqttNetScopedLogger logger, Exception exception, string message, params object[] parameters)
        {
            logger.Publish(MqttNetLogLevel.Error, message, parameters, exception);
        }
    }
}
