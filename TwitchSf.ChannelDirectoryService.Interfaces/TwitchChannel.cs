using System.Runtime.Serialization;

namespace TwitchSf.ChannelDirectoryService.Interfaces
{
    [DataContract]
    public class TwitchChannel
    {
        /// <summary>
        /// ID of the channel, assigned by Twitch
        /// </summary>
        [DataMember]
        public string Id { get; set; }

        /// <summary>
        /// The display name for the user behind the channel
        /// </summary>
        [DataMember]
        public string DisplayName { get; set; }

        /// <summary>
        /// Number of followers a channel has
        /// </summary>
        [DataMember]
        public uint Followers { get; set; }
    }
}