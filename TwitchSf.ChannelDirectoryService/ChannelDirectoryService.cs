using System;
using System.Collections.Concurrent;
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
using SerilogTimings.Extensions;
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

        public async Task<IEnumerable<TwitchChannel>> GetChannelsAsync()
        {
            return await _channelManager.GetAllChannels();
        }

        public Task AddChannelByNameAsync(string channelName)
        {
            return _channelManager.AddChannelByName(channelName);
        }

        public Task<Guid> AddChannelByIdAsync(string channelId)
        {
            throw new NotImplementedException();
        }

        public Task RemoveChannelByName(string channelName)
        {
            return _channelManager.RemoveChannelByName(channelName);
        }

        //internal
    }

    internal interface ITwitchChannelRepository
    {
        Task<bool> TryAddChannel(TwitchChannel channel);
        Task UpdateChannel(TwitchChannel channel);
        Task<IReadOnlyCollection<TwitchChannel>> GetAllChannels();
        Task DeleteChannel(TwitchChannel channel);
        Task DeleteChannelById(string channelId);
        Task DeleteChannelByName(string channelName);
    }

    internal class TwitchChannelRepository : ITwitchChannelRepository, ISubsystem
    {
        private readonly ILogger _log;
        private readonly IReliableStateManager _stateManager;

        public TwitchChannelRepository(ILogger log, IReliableStateManager stateManager)
        {
            _log = log;
            _stateManager = stateManager;
        }

        public async Task<bool> TryAddChannel(TwitchChannel channel)
        {
            var record = new TwitchChannelRecord
            {
                Id = channel.Id,
                DisplayName = channel.DisplayName,
                Followers = channel.Followers
            };

            using (var op = _log.BeginOperation("Adding channel to repository. Channel name {channelName} id {channelId}", channel.DisplayName, channel.Id))
            using (ITransaction tx = _stateManager.CreateTransaction())
            {
                var channelDictionary = await GetChannelDictionary(tx);

                if (!await channelDictionary.TryAddAsync(tx, record.Id, record))
                {
                    return false;
                }

                await tx.CommitAsync()
                    .ContinueWith(task => RepositoryIndexes.Instance.AddRecord(record.Id, record.DisplayName),
                    TaskContinuationOptions.OnlyOnRanToCompletion);

                op.Complete();
            }

            return true;
        }

        public async Task UpdateChannel(TwitchChannel channel)
        {
            /*using (var tx = _stateManager.CreateTransaction())
            {
                var channelDictionary = await GetChannelDictionary(tx);

                channelDictionary.

                await tx.CommitAsync();
            }*/
        }

        public async Task<IReadOnlyCollection<TwitchChannel>> GetAllChannels()
        {
            var channelList = new List<TwitchChannel>();

            using (_log.TimeOperation("Fetching all channels"))
            using (ITransaction tx = _stateManager.CreateTransaction())
            {
                var channelDictionary = await GetChannelDictionary(tx);

                var enumerable = await channelDictionary.CreateEnumerableAsync(tx);
                await enumerable.ForeachAsync(CancellationToken.None, pair =>
                    {
                        channelList.Add(new TwitchChannel
                        {
                            Id = pair.Value.Id,
                            DisplayName = pair.Value.DisplayName,
                            Followers = pair.Value.Followers
                        });
                    });
            }

            return channelList;
        }

        public Task DeleteChannel(TwitchChannel channel)
        {
            return DeleteChannelById(channel.Id);
        }

        public Task DeleteChannelByName(string channelName)
        {
            string channelId;
            if (!RepositoryIndexes.Instance.TryGetChannelIdForName(channelName, out channelId))
            {
                // Let's treat this as non-existing
                _log.Debug("DeleteChannelByName was called with {channelName}, but no ID existed in index", channelName);
                return Task.CompletedTask;
            }

            return DeleteChannelById(channelId);
        }

        public async Task DeleteChannelById(string channelId)
        {
            using (var op = _log.BeginOperation("Removing channel {channelId} from repository", channelId))
            using (ITransaction tx = _stateManager.CreateTransaction())
            {
                var channelDictionary = await GetChannelDictionary(tx);

                ConditionalValue<TwitchChannelRecord> removedRecord = await channelDictionary.TryRemoveAsync(tx, channelId);
                if (!removedRecord.HasValue)
                {
                    return;
                }

                TwitchChannelRecord record = removedRecord.Value;
                await tx.CommitAsync()
                    .ContinueWith(task => RepositoryIndexes.Instance.RemoveRecord(record.Id, record.DisplayName),
                        TaskContinuationOptions.OnlyOnRanToCompletion);

                op.Complete();
            }
        }

        private Task<IReliableDictionary<string, TwitchChannelRecord>> GetChannelDictionary(ITransaction tx)
            => _stateManager.GetOrAddAsync<IReliableDictionary<string, TwitchChannelRecord>>(tx, "ChannelRepository");

        [DataContract]
        private struct TwitchChannelRecord
        {
            [DataMember]
            public string Id { get; set; }
            [DataMember]
            public string DisplayName { get; set; }
            [DataMember]
            public uint Followers { get; set; }
        }

        private class RepositoryIndexes
        {
            public static readonly RepositoryIndexes Instance = new RepositoryIndexes();

            private readonly ConcurrentDictionary<string, string> _channelIdByName =
                new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly ConcurrentDictionary<string, string> _channelNameById =
                new ConcurrentDictionary<string, string>();

            private RepositoryIndexes()
            {}

            public void AddRecord(string channelId, string channelName)
            {
                _channelIdByName[channelName] = channelId;
                _channelNameById[channelId] = channelName;
            }

            public void RemoveRecord(string channelId, string channelName)
            {
                _channelIdByName.TryRemove(channelName, out _);
                _channelNameById.TryRemove(channelId, out _);
            }

            public bool TryGetChannelIdForName(string channelName, out string channelId)
                => _channelIdByName.TryGetValue(channelName, out channelId);
        }

        async Task ISubsystem.Start()
        {
            // Build the dictionary first if it doesn't exist
            using (ITransaction tx = _stateManager.CreateTransaction())
            {
                await GetChannelDictionary(tx);
                await tx.CommitAsync();
            }

            using (_log.TimeOperation("Building lookup for twitch channels"))
            using (ITransaction tx = _stateManager.CreateTransaction())
            {
                var channelDictionary = await GetChannelDictionary(tx);

                var enumerable = await channelDictionary.CreateEnumerableAsync(tx);
                await enumerable.ForeachAsync(CancellationToken.None,
                    pair => RepositoryIndexes.Instance.AddRecord(pair.Key, pair.Value.DisplayName));
            }
        }
    }

    internal class TwitchChannelManager
    {
        private readonly ILogger _log;
        private readonly ITwitchClient _twitchClient;
        private readonly ITwitchChannelRepository _twitchChannelRepository;

        public TwitchChannelManager(ILogger log, ITwitchClient twitchClient, ITwitchChannelRepository twitchChannelRepository)
        {
            _log = log;
            _twitchClient = twitchClient;
            _twitchChannelRepository = twitchChannelRepository;
        }

        public async Task<TwitchChannel> AddChannelByName(string channelName)
        {
            _log.Debug("Adding Twitch channel by name {channelName}", channelName);

            var userEntity = await _twitchClient.GetUserByName(channelName);

            if (userEntity == null)
            {
                throw new AddChannelFailedException(AddChannelFailedReason.ChannelNotFound);
            }

            var channelEntity = await _twitchClient.GetChannelById(userEntity.Id);

            if (channelEntity == null)
            {
                throw new AddChannelFailedException(AddChannelFailedReason.ChannelNotFound);
            }

            var channel = new TwitchChannel
            {
                DisplayName = userEntity.DisplayName,
                Id = userEntity.Id,
                Followers = channelEntity.Followers
            };

            if (!await _twitchChannelRepository.TryAddChannel(channel))
            {
                _log.Debug("Adding channel {channelName} failed", channelName);
                throw new AddChannelFailedException(AddChannelFailedReason.Internal);
            }

            _log.Debug("Channel {channelName} added", channelName);
            return channel;
        }

        public async Task RemoveChannelByName(string channelName)
        {
            _log.Debug("Removing channel by name {channelName}", channelName);

            await _twitchChannelRepository.DeleteChannelByName(channelName);
        }

        public Task<IReadOnlyCollection<TwitchChannel>> GetAllChannels()
            => _twitchChannelRepository.GetAllChannels();
    }
    internal interface ISubsystem
    {
        Task Start();
    }

    internal class TwitchChannelUpdaterSubsystem : ISubsystem
    {
        private readonly ILogger _logger;
        private readonly IReliableStateManager _stateManager;
        private readonly ITwitchClient _twitchClient;
        private Dictionary<string, ChannelUpdateState> _channelUpdateStates = new Dictionary<string, ChannelUpdateState>();

        public TwitchChannelUpdaterSubsystem(IReliableStateManager stateManager, ILogger logger, ITwitchClient twitchClient)
        {
            _stateManager = stateManager;
            _logger = logger;
            _twitchClient = twitchClient;
        }

        public async Task Start()
        {
            _channelUpdateStates.Clear();
        }

        internal class ChannelUpdateState
        {
            public string ChannelId { get; set; }
            public DateTime LastUpdate { get; set; }
        }
    }
}
