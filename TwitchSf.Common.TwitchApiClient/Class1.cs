using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using NTwitch.Rest;
using NTwitch.Rest.API;
using Serilog;
using Serilog.Events;
using SerilogTimings.Extensions;

namespace TwitchSf.Common.TwitchApiClient
{
    public class TwitchModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<TwitchClient>().As<ITwitchClient>();
        }
    }
    public interface ITwitchClient
    {
        Task<TwitchUserEntity> GetUserByName(string userName);
        Task<TwitchChannelEntity> GetChannelById(string id);
    }

    public class TwitchClient : ITwitchClient
    {
        private readonly ILogger _log;
        private readonly TwitchRestClient _restClient = new TwitchRestClient(new TwitchRestConfig
        {
            ClientId = "iwvho9zsd4qecukg9trgi4w3s9ygdv"
        });

        public TwitchClient(ILogger log)
        {
            _log = log;
        }

        public async Task<TwitchUserEntity> GetUserByName(string userName)
        {
            var userCollection = await Request(c => c.GetUsersAsync(userName));

            if (userCollection.Count <= 0)
                return null;

            return userCollection.First().ToEntity();
        }

        public async Task<TwitchChannelEntity> GetChannelById(string id)
        {
            var channel = await Request(c => c.GetChannelAsync(ulong.Parse(id)));

            return channel.ToEntity();
        }

        private async Task<TReturn> Request<TReturn>(Func<TwitchRestClient, Task<TReturn>> action, [CallerMemberName] string requestName = "")
        {
            using (var operation = _log.BeginOperation("Twitch API request {requestName}", requestName))
            {
                try
                {
                    TReturn result = await action(_restClient);

                    if (_log.IsEnabled(LogEventLevel.Verbose))
                    {
                        _log.Verbose("Request {requestName} completed. Result: {@result}", requestName, result);
                    }

                    operation.Complete("ApiResult", result, true);

                    return result;
                }
                catch (NTwitch.Rest.HttpException e)
                {
                    _log.Error(e, "Exception occured on request {requestName}", requestName);
                    throw;
                }
            }
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
                Id = channel.Id.ToString(),
                Followers = channel.Followers
            };
        }
    }

    public class TwitchChannelEntity
    {
        public string Id { get; set; }
        public uint Followers { get; set; }
    }

    public class TwitchUserEntity
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
    }
}
