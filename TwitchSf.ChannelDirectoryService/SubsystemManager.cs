using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using SerilogTimings.Extensions;

namespace TwitchSf.ChannelDirectoryService
{
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
            using (_log.TimeOperation("Starting {0} subsystems", _subsystems.Count))
            {
                _log.Debug("Starting {0} subsystems", _subsystems.Count);

                foreach (ISubsystem subsytem in _subsystems)
                {
                    _log.Debug("Starting subsystem: {subsystemName}", subsytem.GetType());
                    await subsytem.Start();
                }
            }
        }
    }
}