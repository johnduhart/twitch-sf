using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NTwitch.Rest;
using NTwitch.Rest.API;

namespace TwitchSf.Common.TwitchApiClient
{
    public class TwitchClientFactory
    {
        public void Temp()
        {
            var client = new RestApiClient(new TwitchRestConfig
            {
                ClientId = "iwvho9zsd4qecukg9trgi4w3s9ygdv"
            });
            //client.
        }
    }

    public interface ITwitchClient
    {
        Task<TwitchUserEntity> GetUserByName(string userName);
        Task<TwitchChannelEntity> GetChannelById(string id);
    }

    public class TwitchClient : ITwitchClient
    {
        private readonly TwitchRestClient _restClient = new TwitchRestClient(new TwitchRestConfig
        {
            ClientId = "iwvho9zsd4qecukg9trgi4w3s9ygdv"
        });

        public async Task<TwitchUserEntity> GetUserByName(string userName)
        {
            var userCollection = await _restClient.GetUsersAsync(userName);

            if (userCollection.Count <= 0)
                return null;

            return userCollection.First().ToEntity();
        }

        public async Task<TwitchChannelEntity> GetChannelById(string id)
        {
            var channel = await _restClient.GetChannelAsync(ulong.Parse(id));

            return channel.ToEntity();
        }
    }

    internal static class EntityConversionExtensions
    {
        internal static TwitchUserEntity ToEntity(this RestUser rt)
        {
            return new TwitchUserEntity
            {
                DisplayName = rt.DisplayName,
                Id = rt.Id.ToString()
            };
        }

        internal static TwitchChannelEntity ToEntity(this RestChannel channel)
        {
            return new TwitchChannelEntity
            {
                Id = channel.Id.ToString()
            };
        }
    }

    public class TwitchChannelEntity
    {
        public string Id { get; set; }
    }

    public class TwitchUserEntity
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
    }
}
