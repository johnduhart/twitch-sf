using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Remoting;

namespace TwitchSf.ChannelDirectoryService.Interfaces
{
    public interface IChannelDirectoryService : IService
    {
        Task<IEnumerable<TwitchChannel>> GetChannelsAsync();

        Task AddChannelByNameAsync(string channelName);
        Task<Guid> AddChannelByIdAsync(string channelId);
    }
}