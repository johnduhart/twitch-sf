using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Remoting;

namespace TwitchSf.ChannelDirectoryService.Interfaces
{
    public interface IChannelDirectoryService : IService
    {
        Task<IEnumerable<TwitchChannel>> GetChannelsAsync();

        /// <summary>
        /// Attempts to add the given channel to the directory
        /// </summary>
        /// <param name="channelName">Name of the channel.</param>
        /// <returns>The channel information that was added</returns>
        Task<TwitchChannel> AddChannelByNameAsync(string channelName);
        Task<Guid> AddChannelByIdAsync(string channelId);
        Task RemoveChannelByName(string channelName);
    }

    [Serializable]

    public class AddChannelFailedException : Exception
    {
        public AddChannelFailedException(AddChannelFailedReason reason)
            : base(reason.ToString())
        {
            Reason = reason;
        }

        protected AddChannelFailedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            Reason = (AddChannelFailedReason) info.GetByte("Reason");
        }

        public AddChannelFailedReason Reason { get; }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue("Reason", Reason);
        }
    }

    public enum AddChannelFailedReason : byte
    {
        ChannelNotFound,
        Internal
    }
}