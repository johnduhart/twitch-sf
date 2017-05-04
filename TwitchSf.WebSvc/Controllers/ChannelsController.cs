using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Communication.Client;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using TwitchSf.ChannelDirectoryService.Interfaces;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace TwitchSf.WebSvc.Controllers
{
    [Route("api/[controller]")]
    public class ChannelsController : Controller
    {
        // GET: api/values
        [HttpGet]
        public async Task<IEnumerable<TwitchChannel>> Get()
        {
            IChannelDirectoryService channelDirectoryService = ServiceProxy.Create<IChannelDirectoryService>(
                new Uri("fabric:/TwitchSf/ChannelDirectoryService"),
                new ServicePartitionKey(1),
                targetReplicaSelector: TargetReplicaSelector.RandomReplica);

            return await channelDirectoryService.GetChannelsAsync();
        }

        // GET api/values/5
        [HttpGet("{id}")]
        public string Get(int id)
        {
            return "value";
        }

        // POST api/values
        [HttpPost]
        public void Post([FromBody]string value)
        {
        }

        // PUT api/values/5
        [HttpPut("{id}")]
        public void Put(int id, [FromBody]string value)
        {
        }

        // DELETE api/values/5
        [HttpDelete("{id}")]
        public void Delete(int id)
        {
        }
    }
}
