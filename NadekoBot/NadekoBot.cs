﻿using Discord;
using System;
using System.IO;
using Newtonsoft.Json;
using Discord.Commands;
using NadekoBot.Modules;
using Discord.Modules;
using Discord.Audio;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NadekoBot.Classes.JSONModels;
using NadekoBot.Commands;

namespace NadekoBot {
    internal class NadekoBot {
        public static DiscordClient Client;
        public static Credentials Creds { get; set; }
        public static Configuration Config { get; set; }
        public static LocalizedStrings Locale { get; set; } = new LocalizedStrings();
        public static string BotMention { get; set; } = "";

        private static Channel OwnerPrivateChannel { get; set; }

        private static void Main() {
            Console.OutputEncoding = Encoding.Unicode;
            // generate credentials example so people can know about the changes i make
            try {
                File.WriteAllText("credentials_example.json", JsonConvert.SerializeObject(new Credentials(), Formatting.Indented));
                File.WriteAllText("data/config_example.json", JsonConvert.SerializeObject(new Configuration(), Formatting.Indented));
            } catch {
                Console.WriteLine("Failed writing credentials_example.json or data/config_example.json");
            }

            try {
                Config = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText("data/config.json"));
            } catch {
                Console.WriteLine("Failed loading configuration.");
            }

            try {
                //load credentials from credentials.json
                Creds = JsonConvert.DeserializeObject<Credentials>(File.ReadAllText("credentials.json"));
            } catch (Exception ex) {
                Console.WriteLine($"Failed to load stuff from credentials.json, RTFM\n{ex.Message}");
                Console.ReadKey();
                return;
            }

            //if password is not entered, prompt for password
            if (string.IsNullOrWhiteSpace(Creds.Password)) {
                Console.WriteLine("Password blank. Please enter your password:\n");
                Creds.Password = Console.ReadLine();
            }

            Console.WriteLine(string.IsNullOrWhiteSpace(Creds.GoogleAPIKey)
                ? "No google api key found. You will not be able to use music and links won't be shortened."
                : "Google API key provided.");
            Console.WriteLine(string.IsNullOrWhiteSpace(Creds.TrelloAppKey)
                ? "No trello appkey found. You will not be able to use trello commands."
                : "Trello app key provided.");
            Console.WriteLine(Config.ForwardMessages != true
                ? "Not forwarding messages."
                : "Forwarding private messages to owner.");
            Console.WriteLine(string.IsNullOrWhiteSpace(Creds.SoundCloudClientID)
                ? "No soundcloud Client ID found. Soundcloud streaming is disabled."
                : "SoundCloud streaming enabled.");

            BotMention = $"<@{Creds.BotId}>";

            //create new discord client and log
            Client = new DiscordClient(new DiscordConfigBuilder() {
                MessageCacheSize = 10,
                ConnectionTimeout = 60000,
                LogLevel = LogSeverity.Warning,
                LogHandler = (s, e) =>
                    Console.WriteLine($"Severity: {e.Severity}" +
                                      $"Message: {e.Message}" +
                                      $"ExceptionMessage: {e.Exception?.Message ?? "-"}"),
            });

            //create a command service
            var commandService = new CommandService(new CommandServiceConfigBuilder {
                AllowMentionPrefix = false,
                CustomPrefixHandler = m => 0,
                HelpMode = HelpMode.Disabled,
                ErrorHandler = async (s, e) => {
                    if (e.ErrorType != CommandErrorType.BadPermissions)
                        return;
                    if (string.IsNullOrWhiteSpace(e.Exception?.Message))
                        return;
                    try {
                        await e.Channel.SendMessage(e.Exception.Message);
                    } catch { }
                }
            });

            //reply to personal messages and forward if enabled.
            Client.MessageReceived += Client_MessageReceived;

            //add command service
            Client.AddService<CommandService>(commandService);

            //create module service
            var modules = Client.AddService<ModuleService>(new ModuleService());

            //add audio service
            Client.AddService<AudioService>(new AudioService(new AudioServiceConfigBuilder() {
                Channels = 2,
                EnableEncryption = false,
                EnableMultiserver = true,
                Bitrate = 128,
            }));

            //install modules
            modules.Add(new Administration(), "Administration", ModuleFilter.None);
            modules.Add(new Help(), "Help", ModuleFilter.None);
            modules.Add(new PermissionModule(), "Permissions", ModuleFilter.None);
            modules.Add(new Conversations(), "Conversations", ModuleFilter.None);
            modules.Add(new Gambling(), "Gambling", ModuleFilter.None);
            modules.Add(new Games(), "Games", ModuleFilter.None);
            modules.Add(new Music(), "Music", ModuleFilter.None);
            modules.Add(new Searches(), "Searches", ModuleFilter.None);
            modules.Add(new NSFW(), "NSFW", ModuleFilter.None);
            modules.Add(new ClashOfClans(), "ClashOfClans", ModuleFilter.None);
            if (!string.IsNullOrWhiteSpace(Creds.TrelloAppKey))
                modules.Add(new Trello(), "Trello", ModuleFilter.None);

            //run the bot
            Client.ExecuteAndWait(async () => {
                try {
                    await Client.Connect(Creds.Username, Creds.Password);
                } catch (Exception ex) {
                    Console.WriteLine($"Probably wrong EMAIL or PASSWORD.\n{ex.Message}");
                    Console.ReadKey();
                    Console.WriteLine(ex);
                    Console.ReadKey();
                    return;
                }
                Console.WriteLine("-----------------");
                Console.WriteLine(await NadekoStats.Instance.GetStats());
                Console.WriteLine("-----------------");

                try {
                    OwnerPrivateChannel = await Client.CreatePrivateChannel(Creds.OwnerIds[0]);
                } catch {
                    Console.WriteLine("Failed creating private channel with the first owner listed in credentials.json");
                }

                Classes.Permissions.PermissionsHandler.Initialize();

                Client.ClientAPI.SendingRequest += (s, e) => {
                    var request = e.Request as Discord.API.Client.Rest.SendMessageRequest;
                    if (request == null) return;
                    request.Content = request.Content?.Replace("@everyone", "@everyοne") ?? "_error_";
                    if (string.IsNullOrWhiteSpace(request.Content))
                        e.Cancel = true;
                };
            });
            Console.WriteLine("Exiting...");
            Console.ReadKey();
        }

        public static bool IsOwner(ulong id) => Creds.OwnerIds.Contains(id);

        public static bool IsOwner(User u) => IsOwner(u.Id);

        public async Task SendMessageToOwner(string message) {
            if (Config.ForwardMessages && OwnerPrivateChannel != null)
                await OwnerPrivateChannel.SendMessage(message);
        }

        private static bool repliedRecently = false;
        private static async void Client_MessageReceived(object sender, MessageEventArgs e) {
            try {
                if (e.Server != null || e.User.Id == Client.CurrentUser.Id) return;
                if (PollCommand.ActivePolls.SelectMany(kvp => kvp.Key.Users.Select(u => u.Id)).Contains(e.User.Id)) return;
                if (ConfigHandler.IsBlackListed(e))
                    return;

                if (!NadekoBot.Config.DontJoinServers) {
                    try {
                        await (await Client.GetInvite(e.Message.Text)).Accept();
                        await e.Channel.SendMessage("I got in!");
                        return;
                    } catch {
                        if (e.User.Id == 109338686889476096) { //carbonitex invite
                            await e.Channel.SendMessage("Failed to join the server.");
                            return;
                        }
                    }
                }

                if (Config.ForwardMessages && OwnerPrivateChannel != null)
                    await OwnerPrivateChannel.SendMessage(e.User + ": ```\n" + e.Message.Text + "\n```");

                if (repliedRecently) return;

                repliedRecently = true;
                await e.Channel.SendMessage(HelpCommand.HelpString);
                await Task.Delay(2000);
                repliedRecently = false;
            } catch { }
        }
    }
}

//95520984584429568 meany