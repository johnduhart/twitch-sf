﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
                    .WriteTo.Sink<EventSourceSink>()
                    .CreateLogger();

                // The ServiceManifest.XML file defines one or more service type names.
                // Registering a service maps a service type name to a .NET type.
                // When Service Fabric creates an instance of this service type,
                // an instance of the class is created in this host process.

                ServiceRuntime.RegisterServiceAsync("ChannelDirectoryServiceType",
                    context => new ChannelDirectoryService(context)).GetAwaiter().GetResult();

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
    }

    internal class EventSourceSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            ServiceEventSource.Current.Message(logEvent.RenderMessage());
        }
    }
}
