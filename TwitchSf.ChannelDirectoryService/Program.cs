using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutofacSerilogIntegration;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Services.Runtime;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TwitchSf.ChannelDirectoryService
{
    internal static class Program
    {
        /// <summary>
        /// This is the entry point of the service host process.
        /// </summary>
        private static void Main()
        {
            try
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.Sink<EventSourceSink>()
                    .CreateLogger();

                var container = BuildRootContainer();

                // The ServiceManifest.XML file defines one or more service type names.
                // Registering a service maps a service type name to a .NET type.
                // When Service Fabric creates an instance of this service type,
                // an instance of the class is created in this host process.

                ServiceRuntime.RegisterServiceAsync("ChannelDirectoryServiceType",
                    context =>
                    {
                        var scope = container.BeginLifetimeScope(builder =>
                        {
                            builder.RegisterInstance(context)
                                .AsSelf()
                                .ExternallyOwned();

                            builder.Register(c => new ReliableStateManager(context, null))
                                .AsSelf()
                                .As<IReliableStateManager>()
                                .SingleInstance();

                            builder.RegisterType<ChannelDirectoryService>()
                                .AsSelf()
                                .SingleInstance();

                            builder.RegisterType<SubsystemManager>()
                                .AsSelf()
                                .SingleInstance();

                            builder.RegisterType<TwitchChannelUpdaterSubsystem>().As<ISubsystem>().InstancePerDependency();

                            var serviceLogger = Log.Logger.ForContext(new ServiceContextLogEventEnricher(context));
                            builder.RegisterLogger(serviceLogger);
                        });

                        return scope.Resolve<ChannelDirectoryService>();
                    }).GetAwaiter().GetResult();

                ServiceEventSource.Current.ServiceTypeRegistered(Process.GetCurrentProcess().Id, typeof(ChannelDirectoryService).Name);

                // Prevents this host process from terminating so services keep running.
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception e)
            {
                ServiceEventSource.Current.ServiceHostInitializationFailed(e.ToString());
                throw;
            }
        }

        private static IContainer BuildRootContainer()
        {
            var builder = new ContainerBuilder();

            return builder.Build();
        }
    }

    internal class ServiceContextLogEventEnricher : ILogEventEnricher
    {
        private readonly ServiceFabricInfo _serviceInfo;
        private LogEventProperty _serviceInfoProperty;

        public ServiceContextLogEventEnricher(StatefulServiceContext context)
        {
            _serviceInfo = new ServiceFabricInfo(context);
        }

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (_serviceInfoProperty == null)
                _serviceInfoProperty = propertyFactory.CreateProperty("ServiceFabric", _serviceInfo, true);

            logEvent.AddPropertyIfAbsent(_serviceInfoProperty);
        }

        [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local",
            Justification = "Properties are used by Serilog")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Local")]
        private class ServiceFabricInfo
        {
            public ServiceFabricInfo(StatefulServiceContext context)
            {
                ServiceName = context.ServiceName.ToString();
                ServiceTypeName = context.ServiceTypeName;
                ReplicaId = context.ReplicaId;
                PartitionId = context.PartitionId;
                ApplicationName = context.CodePackageActivationContext.ApplicationName;
                ApplicationTypeName = context.CodePackageActivationContext.ApplicationTypeName;
                NodeName = context.NodeContext.NodeName;
            }

            public string ServiceName { get; }
            public string ServiceTypeName { get; }
            public long ReplicaId { get; }
            public Guid PartitionId { get; }
            public string ApplicationName { get; }
            public string ApplicationTypeName { get; }
            public string NodeName { get; }
        }
    }

    internal class EventSourceSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            ServiceEventSource.Current.Message(logEvent.RenderMessage());
        }
    }
}
