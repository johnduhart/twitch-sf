using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Data.Notifications;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Remoting.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Serilog;
using Serilog.Core;
using TwitchSf.ChannelDirectoryService.Interfaces;
using TwitchSf.Common.ServiceFabric;
using TwitchSf.Common.TwitchApiClient;

namespace TwitchSf.ChannelDirectoryService
{
    /// <summary>
    /// An instance of this class is created for each service replica by the Service Fabric runtime.
    /// </summary>
    internal sealed class ChannelDirectoryService : StatefulService, IChannelDirectoryService
    {
        private readonly SubsystemManager _subsystemManager;
        private readonly TwitchChannelManager _channelManager;

        public ChannelDirectoryService(StatefulServiceContext serviceContext,
            ReliableStateManager reliableStateManagerReplica, SubsystemManager subsystemManager, TwitchChannelManager channelManager) : base(serviceContext,
            reliableStateManagerReplica)
        {
            _subsystemManager = subsystemManager;
            _channelManager = channelManager;
        }

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
            await _subsystemManager.StartAll();

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

        public Task AddChannelByNameAsync(string channelName)
        {
            return _channelManager.AddChannelByName(channelName);
        }

        public Task<Guid> AddChannelByIdAsync(string channelId)
        {
            throw new NotImplementedException();
        }

        //internal
    }

    internal class TwitchChannelManager
    {
        private readonly ILogger _log;
        private readonly ITwitchClient _twitchClient;
        private readonly IReliableStateManager _stateManager;

        public TwitchChannelManager(ILogger log, ITwitchClient twitchClient, IReliableStateManager stateManager)
        {
            _log = log;
            _twitchClient = twitchClient;
            _stateManager = stateManager;
        }

        public async Task AddChannelByName(string channelName)
        {
            _log.Debug("Adding Twitch channel by name {channelName}", channelName);

            var userEntity = await _twitchClient.GetUserByName(channelName);

            if (userEntity == null)
            {
                // IDK
                return;
            }

            var channelEntity = await _twitchClient.GetChannelById(userEntity.Id);

            if (channelEntity == null)
            {
                // IDK
                return;
            }

            var channelState = new TwitchChannelState(userEntity.DisplayName, channelEntity.Id);

            using (var tx = _stateManager.CreateTransaction())
            {
                var channelList = await _stateManager.GetChannelList(tx);

                if (await channelList.ContainsKeyAsync(tx, channelState.Id))
                {
                    _log.Information("Attempted to add channel {channelId}, which already existed", channelState.Id);
                    return;
                }

                await channelList.AddAsync(tx, channelState.Id, channelState);

                await tx.CommitAsync();
            }

            _log.Debug("Channel {channelName} added", channelName);
        }
    }

    internal static class StateConstants
    {
        public const string ChannelList = "channelList";
    }

    internal static class StateManagerExtensions
    {
        public static Task<IReliableDictionary<string, TwitchChannelState>> GetChannelList(this IReliableStateManager stateManager)
            => stateManager.GetOrAddAsync<IReliableDictionary<string, TwitchChannelState>>(StateConstants.ChannelList);

        public static Task<IReliableDictionary<string, TwitchChannelState>> GetChannelList(this IReliableStateManager stateManager, ITransaction transaction)
            => stateManager.GetOrAddAsync<IReliableDictionary<string, TwitchChannelState>>(transaction, StateConstants.ChannelList);
    }

    internal class SubsystemManager
    {
        private readonly ILogger _log;
        private readonly HashSet<ISubsystem> _subsystems;

        public SubsystemManager(IEnumerable<ISubsystem> subsystems, ILogger log)
        {
            _log = log;
            _subsystems = new HashSet<ISubsystem>(subsystems);
        }

        public async Task StartAll()
        {
            _log.Debug("Starting {0} subsystems", _subsystems.Count);

            foreach (ISubsystem subsytem in _subsystems)
            {
                _log.Debug("Starting subsystem: {subsystemName}", subsytem.GetType());
                await subsytem.Start();
            }
        }
    }

    internal interface ISubsystem
    {
        Task Start();
    }

    internal class TwitchChannelUpdaterSubsystem : ISubsystem
    {
        private readonly ILogger _logger;
        private readonly IReliableStateManager _stateManager;
        private Dictionary<string, ChannelUpdateState> _channelUpdateStates = new Dictionary<string, ChannelUpdateState>();

        public TwitchChannelUpdaterSubsystem(IReliableStateManager stateManager, ILogger logger)
        {
            _stateManager = stateManager;
            _logger = logger;
        }

        public async Task Start()
        {
            _channelUpdateStates.Clear();

            var channelList = await _stateManager.GetChannelList();

            using (var transaction = _stateManager.CreateTransaction())
            {
                var enumerable = await channelList.CreateEnumerableAsync(transaction, EnumerationMode.Unordered);
                await enumerable.ForeachAsync(CancellationToken.None,
                    pair => _channelUpdateStates.Add(pair.Key, new ChannelUpdateState()));
            }

            channelList.DictionaryChanged += ChannelListChanged;
        }

        private void ChannelListChanged(object sender, NotifyDictionaryChangedEventArgs<string, TwitchChannelState> notifyDictionaryChangedEventArgs)
        {
            _logger.Debug("Recieved dictionary notification {notificationType}", notifyDictionaryChangedEventArgs.Action);
            // TODO: Implement
        }

        internal class ChannelUpdateState
        {
            //
        }
    }

    [DataContract]
    internal struct TwitchChannelState
    {
        [DataMember]
        public string DisplayName { get; private set; }

        [DataMember]
        public string Id { get; private set; }

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
