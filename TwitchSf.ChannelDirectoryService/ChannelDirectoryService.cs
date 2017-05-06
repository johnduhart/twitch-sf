using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Serilog;
using Serilog.Core;
using TwitchSf.ChannelDirectoryService.Interfaces;
using TwitchSf.Common.TwitchApiClient;

namespace TwitchSf.ChannelDirectoryService
{
    /// <summary>
    /// An instance of this class is created for each service replica by the Service Fabric runtime.
    /// </summary>
    internal sealed class ChannelDirectoryService : StatefulService, IChannelDirectoryService
    {
        public ChannelDirectoryService(StatefulServiceContext context)
            : base(context)
        { }

        /// <summary>
        /// Optional override to create listeners (e.g., HTTP, Service Remoting, WCF, etc.) for this service replica to handle client or user requests.
        /// </summary>
        /// <remarks>
        /// For more information on service communication, see https://aka.ms/servicefabricservicecommunication
        /// </remarks>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new[]
            {
                new ServiceReplicaListener(this.CreateServiceRemotingListener),
            };
        }

        /// <summary>
        /// This is the main entry point for your service replica.
        /// This method executes when this replica of your service becomes primary and has write status.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service replica.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        public Task<IEnumerable<TwitchChannel>> GetChannelsAsync()
        {
            return Task.FromResult<IEnumerable<TwitchChannel>>(new List<TwitchChannel>
            {
                new TwitchChannel
                {
                    Id = "127506955",
                    DisplayName = "playBATTLEGROUNDS"
                },
                new TwitchChannel
                {
                    Id = "36769016",
                    DisplayName = "TimTheTatman"
                }
            });
        }

        public async Task<Guid> AddChannelByNameAsync(string channelName)
        {
            Log.Debug("Adding Twitch channel by name {channelName}", channelName);

            var twitchClient = new TwitchClient();
            var userEntity = await twitchClient.GetUserByName(channelName);

            if (userEntity == null)
            {
                // IDK
                return Guid.Empty;
            }

            var channelEntity = await twitchClient.GetChannelById(userEntity.Id);

            if (channelEntity == null)
            {
                // IDK
                return Guid.Empty;
            }

            var channelState = new TwitchChannelState(userEntity.DisplayName, channelEntity.Id);

            using (var tx = StateManager.CreateTransaction())
            {
                var channelList = await StateManager.GetOrAddAsync<IReliableDictionary<string, TwitchChannelState>>(tx, "channelList");

                if (await channelList.ContainsKeyAsync(tx, channelState.Id))
                {
                    Log.Information("Attempted to add channel {channelId}, which already existed", channelState.Id);
                    return Guid.Empty;
                }

                await channelList.AddAsync(tx, channelState.Id, channelState);

                await tx.CommitAsync();
                throw new NotImplementedException();
            }
        }

        public Task<Guid> AddChannelByIdAsync(string channelId)
        {
            throw new NotImplementedException();
        }
    }

    internal struct TwitchChannelState
    {
        public string DisplayName { get; }
        public string Id { get; }

        public TwitchChannelState(string displayName, string id)
        {
            DisplayName = displayName;
            Id = id;
        }
    }

    [DataContract]
    internal class ChannelDiscoveryTask
    {
        [DataMember]
        public string ChannelName { get; }

        public ChannelDiscoveryTask(string channelName)
        {
            ChannelName = channelName;
        }
    }

    internal class ChannelDiscoveryTaskExecuter
    {
        private readonly ITwitchClient _twitchClient;

        public ChannelDiscoveryTaskExecuter(ITwitchClient twitchClient)
        {
            _twitchClient = twitchClient;
        }

        public async Task ExecuteTask(ChannelDiscoveryTask discoveryTask)
        {
            var user = await _twitchClient.GetUserByName(discoveryTask.ChannelName);

            if (user == null)
            {
                // IDK
                return;
            }

            var channel = await _twitchClient.GetChannelById(user.Id);
        }
    }
}
