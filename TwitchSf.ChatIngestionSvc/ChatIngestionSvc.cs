using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

namespace TwitchSf.ChatIngestionSvc
{
    /// <summary>
    /// An instance of this class is created for each service replica by the Service Fabric runtime.
    /// </summary>
    internal sealed class ChatIngestionSvc : StatefulService
    {
        private const string ChannelDictionary = "channels";

        private TwitchChatConfiguration _chatConfiguration;
        private TwitchChatClient _chatClient;

        public ChatIngestionSvc(StatefulServiceContext context)
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
            return new ServiceReplicaListener[0];
        }

        /// <summary>
        /// This is the main entry point for your service replica.
        /// This method executes when this replica of your service becomes primary and has write status.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service replica.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            LoadChatConfiguration();
            await PrepopulateChannelList();

            _chatClient = new TwitchChatClient(_chatConfiguration);
            _chatClient.CanJoinChannels += () => Task.Run(JoinChannels);

            await _chatClient.ConnectAsync();

            //var myDictionary = await this.StateManager.GetOrAddAsync<IReliableDictionary<string, long>>("myDictionary");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TwitchChatMessage[] chatMessages = await _chatClient.ReadLinesAsync(cancellationToken);

                if (chatMessages.Length > 0)
                {
                    ServiceEventSource.Current.ServiceMessage(Context, "Recieved {0} chat messages", chatMessages.Length.ToString());
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }

        private void LoadChatConfiguration()
        {
            var configurationPackage = Context.CodePackageActivationContext.GetConfigurationPackageObject("Config");
            ConfigurationSection chatSection = configurationPackage.Settings.Sections["ChatConfig"];
            _chatConfiguration = new TwitchChatConfiguration(chatSection.Parameters["Nickname"].Value, chatSection.Parameters["OAuth"].Value);
        }

        private async Task PrepopulateChannelList()
        {
            var channelDictionary = await StateManager.GetOrAddAsync<IReliableDictionary<string, ChannelState>>(ChannelDictionary);

            using (var tx = StateManager.CreateTransaction())
            {
                long count = await channelDictionary.GetCountAsync(tx);

                if (count == 0)
                {
                    ServiceEventSource.Current.ServiceMessage(Context, "No channels have been defined for this service, default channels will be added.");

                    await channelDictionary.AddAsync(tx, "savjz", new ChannelState());
                    await channelDictionary.AddAsync(tx, "summit1g", new ChannelState());
                    await channelDictionary.AddAsync(tx, "timthetatman", new ChannelState());
                    await channelDictionary.AddAsync(tx, "giantwaffle", new ChannelState());
                    await channelDictionary.AddAsync(tx, "thesleepydwarf_", new ChannelState());
                    await channelDictionary.AddAsync(tx, "pmsproxy", new ChannelState());
                }

                await tx.CommitAsync();
            }
        }

        private async Task JoinChannels()
        {
            var channelDictionary = await StateManager.GetOrAddAsync<IReliableDictionary<string, ChannelState>>(ChannelDictionary);

            using (var tx = StateManager.CreateTransaction())
            {
                var enumerable = await channelDictionary.CreateEnumerableAsync(tx);

                await enumerable.ForeachAsync(CancellationToken.None,
                    async pair => await _chatClient.JoinChannel("#" + pair.Key.ToLowerInvariant()));
            }
        }
    }

    internal class TwitchChatConfiguration
    {
        public TwitchChatConfiguration(string nickname, string oAuth)
        {
            Nickname = nickname;
            OAuth = oAuth;
        }

        public string Nickname { get; }
        public string OAuth { get; }
    }

    [DataContract]
    struct ChannelState
    {

    }

    internal class TwitchChatClient
    {
        private readonly TwitchChatConfiguration _chatConfiguration;
        private static readonly string ChatServer = "irc.chat.twitch.tv";

        public event Action CanJoinChannels;

        private StreamReader _streamReader;
        private StreamWriter _streamWriter;
        private TcpClient _tcpClient;

        public TwitchChatClient(TwitchChatConfiguration chatConfiguration)
        {
            _chatConfiguration = chatConfiguration;
        }

        public async Task ConnectAsync()
        {
            ServiceEventSource.Current.ChatConnectStart(ChatServer);

            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(ChatServer, 6667);
            NetworkStream stream = _tcpClient.GetStream();

            _streamReader = new StreamReader(stream, new UTF8Encoding(false));
            _streamWriter = new StreamWriter(stream, new UTF8Encoding(false));

            await _streamWriter.WriteLineAsync("PASS " + _chatConfiguration.OAuth);
            await _streamWriter.WriteLineAsync("NICK " + _chatConfiguration.Nickname);
            await _streamWriter.FlushAsync();

            ServiceEventSource.Current.ChatConnectStop(ChatServer);
        }

        public async Task<TwitchChatMessage[]> ReadLinesAsync(CancellationToken cancellationToken)
        {
            Task timeoutTask = Task.Delay(250, cancellationToken);
            List<TwitchChatMessage> chatMessages = null;

            while ((!_streamReader.EndOfStream || _tcpClient.Available > 0) && !timeoutTask.IsCompleted)
            {
                Task<string> streamTask = _streamReader.ReadLineAsync();
                var completedTask = await Task.WhenAny(streamTask, timeoutTask);
                if (completedTask != streamTask)
                    break;

                var message = await HandleLine(streamTask.Result);
                if (message.HasValue)
                {
                    if (message.Value.Command == "PRIVMSG")
                    {
                        TwitchChatMessage chatMessage = ParseMessage(message.Value);
                        if (chatMessages == null)
                            chatMessages = new List<TwitchChatMessage>();

                        chatMessages.Add(chatMessage);
                    }
                    else
                    {
                        ServiceEventSource.Current.Message("Unkown IRC command type {0}", message.Value.Command);
                    }
                }

                if (chatMessages != null && chatMessages.Count > 100)
                {
                    // 100 Mesages is enough
                    break;
                }
            }

            return chatMessages?.ToArray() ?? new TwitchChatMessage[0];
        }

        public async Task JoinChannel(string channelName)
        {
            await _streamWriter.WriteLineAsync("JOIN " + channelName);
            await _streamWriter.FlushAsync();
        }

        private async Task<IrcMessage?> HandleLine(string line)
        {
            if (line.StartsWith(":tmi.twitch.tv"))
            {
                await HandleServerLine(line);
                return null;
            }

            if (line.StartsWith("PING"))
            {
                await HandlePingLine(line);
                return null;
            }

            string twitchPart = string.Empty;
            if (line[0] == '@')
            {
                string[] parts = line.Split(new[] { ' ' }, 2);
                twitchPart = parts[0];
                line = parts[1];
            }

            // Extract prefix (user host)
            string prefix = String.Empty;
            if (line[0] == ':')
            {
                int firstSpaceIndex = line.IndexOf(' ');
                prefix = line.Substring(1, firstSpaceIndex - 1);
                line = line.Substring(firstSpaceIndex + 1);
            }

            // extract command
            int spaceIndex = line.IndexOf(' ');
            string command = line.Substring(0, spaceIndex);
            string paramsLine = line.Substring(command.Length + 1);

            // Extract parameters from message.
            // Each parameter is separated by single space, except last one, which may contain spaces if it
            // is prefixed by colon.
            var parameters = new string[15];
            int paramStartIndex, paramEndIndex = -1;
            var lineColonIndex = paramsLine.IndexOf(" :");
            if (lineColonIndex == -1 && !paramsLine.StartsWith(":"))
                lineColonIndex = paramsLine.Length;
            for (var i = 0; i < parameters.Length; i++)
            {
                paramStartIndex = paramEndIndex + 1;
                paramEndIndex = paramsLine.IndexOf(' ', paramStartIndex);
                if (paramEndIndex == -1)
                    paramEndIndex = paramsLine.Length;
                if (paramEndIndex > lineColonIndex)
                {
                    paramStartIndex++;
                    paramEndIndex = paramsLine.Length;
                }
                parameters[i] = paramsLine.Substring(paramStartIndex, paramEndIndex - paramStartIndex);
                if (paramEndIndex == paramsLine.Length)
                    break;
            }

            return new IrcMessage(twitchPart, prefix, command, parameters);
        }

        private TwitchChatMessage ParseMessage(IrcMessage message)
        {
            if (message.Command != "PRIVMSG")
            {
                //Console.WriteLine($"Unknown command type {message.Command}", Color.Orange);
                return null;
            }

            var chatMessage = new TwitchChatMessage();

            // Get the nickname from the prefix
            string nickname = message.Prefix.Split('!')[0];
            chatMessage.Nickname = nickname;
            chatMessage.Message = message.Parameters[1];
            chatMessage.IrcChannel = message.Parameters[0];

            //Console.Write($"{message.Parameters[0]}: ", Color.MediumSpringGreen);
            //Console.Write($"<{nickname}> ", Color.Aqua);
            //Console.WriteLine(message.Parameters[1], Color.LightSkyBlue);

            if (!string.IsNullOrEmpty(message.TwitchPart))
            {
                string twitchPart = message.TwitchPart.TrimStart('@');
                string[] parameters = twitchPart.Split(';');

                foreach (string parameter in parameters)
                {
                    (string name, string value) = ParseParameter(parameter);

                    switch (name)
                    {
                        case "color":
                            chatMessage.DisplayColor = value;
                            break;
                        case "display-name":
                            chatMessage.DisplayName = value;
                            break;
                        case "user-id":
                            chatMessage.UserId = int.Parse(value);
                            break;
                        case "room-id":
                            chatMessage.RoomId = int.Parse(value);
                            break;
                        case "user-type":
                            chatMessage.UserType = value;
                            break;
                        case "id":
                            chatMessage.MessageId = Guid.Parse(value);
                            break;
                        case "badges":
                            chatMessage.Badges = value.Split(',');
                            break;
                        case "mod":
                            chatMessage.IsMod = value == "1";
                            break;
                        case "subscriber":
                            chatMessage.IsSubsrciber = value == "1";
                            break;
                        case "turbo":
                            chatMessage.IsTurbo = value == "1";
                            break;
                        case "emotes":
                            chatMessage.Emotes = ParseEmotes(value);
                            break;
                        case "tmi-sent-ts":
                            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                            DateTime ts = epoch.AddMilliseconds(double.Parse(value));
                            chatMessage.Timestamp = ts;
                            break;
                        default:
                            //Console.WriteLine($"Unkown parameter: {name}={value}", Color.GreenYellow);
                            break;

                    }
                    //Console.WriteLine($"\t{parameter}", Color.Navy);
                }
            }

            // Fallback to using nickname
            if (chatMessage.DisplayName == null)
                chatMessage.DisplayName = chatMessage.Nickname;

            return chatMessage;

            //_logWriter.AddMessage(chatMessage);
            //Console.WriteLine(JsonConvert.SerializeObject(chatMessage), Color.HotPink);
        }

        private ChatEmote[] ParseEmotes(string value)
        {
            if (string.IsNullOrEmpty(value))
                return new ChatEmote[0];

            string[] emoteStrings = value.Split('/');
            var emotes = new List<ChatEmote>();

            foreach (string emoteString in emoteStrings)
            {
                // Split off the emote id
                int seperatorIndex = emoteString.IndexOf(':');
                int emoteId = int.Parse(emoteString.Substring(0, seperatorIndex));

                foreach (string usageString in emoteString.Substring(seperatorIndex + 1).Split(','))
                {
                    string[] usageParts = usageString.Split('-');
                    emotes.Add(new ChatEmote(emoteId, ushort.Parse(usageParts[0]), ushort.Parse(usageParts[0])));
                }
            }

            return emotes.ToArray();
        }

        private static (string, string) ParseParameter(string parameter)
        {
            string[] parts = parameter.Split(new[] { '=' }, 2);

            if (parts.Length == 1)
                return (parts[0], string.Empty);

            return (parts[0], parts[1]);
        }

        private async Task HandleServerLine(string line)
        {
            string[] parts = line.Split(new[] { ' ' }, 4);

            if (parts[1] == "376")
            {
                await _streamWriter.WriteLineAsync("CAP REQ :twitch.tv/tags");
                await _streamWriter.WriteLineAsync("CAP REQ :twitch.tv/commands");
                await _streamWriter.FlushAsync();

                OnCanJoinChannels();
            }
        }

        private async Task HandlePingLine(string line)
        {
            int pos = line.IndexOf(':');

            string str = "PONG " + line.Substring(pos + 1);
            Console.WriteLine($"PONG ({str})");
            await _streamWriter.WriteLineAsync(str);
            await _streamWriter.FlushAsync();
        }


        private struct IrcMessage
        {
            public IrcMessage(string twitchPart, string prefix, string command, string[] parameters)
            {
                TwitchPart = twitchPart;
                Prefix = prefix;
                Command = command;
                Parameters = parameters;
            }

            public string TwitchPart { get; }
            public string Prefix { get; }
            public string Command { get; }
            public string[] Parameters { get; }
        }

        protected virtual void OnCanJoinChannels()
        {
            CanJoinChannels?.Invoke();
        }
    }

    class TwitchChatMessage
    {
        public Guid MessageId { get; set; }
        public int UserId { get; set; }
        public int RoomId { get; set; }

        public string DisplayName { get; set; }
        public string Nickname { get; set; }
        public string DisplayColor { get; set; }
        public string UserType { get; set; }
        public string[] Badges { get; set; }
        public bool IsMod { get; set; }
        public bool IsSubsrciber { get; set; }
        public bool IsTurbo { get; set; }

        public string IrcChannel { get; set; }
        public string Message { get; set; }
        public ChatEmote[] Emotes { get; set; }
        public DateTime Timestamp { get; set; }
    }

    struct ChatEmote
    {
        public ChatEmote(int emoteId, ushort startIndex, ushort stopIndex)
        {
            EmoteId = emoteId;
            StartIndex = startIndex;
            StopIndex = stopIndex;
        }

        public int EmoteId { get; }

        public ushort StartIndex { get; }

        public ushort StopIndex { get; }
    }

    public static class IAsyncEnumerableExtensions
    {
        /// <summary>
        /// Wraps an IAsyncEnumerable with a regular synchronous IEnumerable.
        /// This can be used for performing LINQ queries on Reliable Collections.
        /// However, this wrapper waits synchronously on IAsyncEnumerable's MoveNextAsync call when advancing the enumerator.
        /// </summary>
        /// <typeparam name="TSource"></typeparam>
        /// <param name="source"></param>
        /// <returns></returns>
        public static IEnumerable<TSource> ToEnumerable<TSource>(this IAsyncEnumerable<TSource> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return new AsyncEnumerableWrapper<TSource>(source);
        }

        /// <summary>
        /// Performs an asynchronous for-each loop on an IAsyncEnumerable.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="instance"></param>
        /// <param name="token"></param>
        /// <param name="doSomething"></param>
        /// <returns></returns>
        public static async Task ForeachAsync<T>(this IAsyncEnumerable<T> instance, CancellationToken cancellationToken, Action<T> doSomething)
        {
            using (IAsyncEnumerator<T> e = instance.GetAsyncEnumerator())
            {
                while (await e.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    doSomething(e.Current);
                }
            }
        }

        /// <summary>
        /// Counts the number of items that pass the given predicate.
        /// </summary>
        /// <typeparam name="TSource"></typeparam>
        /// <param name="source"></param>
        /// <param name="predicate"></param>
        /// <returns></returns>
        public static async Task<int> CountAsync<TSource>(this IAsyncEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            int count = 0;
            using (IAsyncEnumerator<TSource> asyncEnumerator = source.GetAsyncEnumerator())
            {
                while (await asyncEnumerator.MoveNextAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (predicate(asyncEnumerator.Current))
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }

    internal struct AsyncEnumerableWrapper<TSource> : IEnumerable<TSource>
    {
        private IAsyncEnumerable<TSource> source;

        public AsyncEnumerableWrapper(IAsyncEnumerable<TSource> source)
        {
            this.source = source;
        }

        public IEnumerator<TSource> GetEnumerator()
        {
            return new AsyncEnumeratorWrapper<TSource>(this.source.GetAsyncEnumerator());
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }

    internal struct AsyncEnumeratorWrapper<TSource> : IEnumerator<TSource>
    {
        private IAsyncEnumerator<TSource> source;

        public AsyncEnumeratorWrapper(IAsyncEnumerator<TSource> source)
        {
            this.source = source;
            this.Current = default(TSource);
        }

        public TSource Current { get; private set; }

        object IEnumerator.Current
        {
            get { throw new NotImplementedException(); }
        }

        public void Dispose()
        {
        }

        public bool MoveNext()
        {
            if (!this.source.MoveNextAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return false;
            }

            this.Current = this.source.Current;
            return true;
        }

        public void Reset()
        {
            throw new NotImplementedException();
        }
    }
}
