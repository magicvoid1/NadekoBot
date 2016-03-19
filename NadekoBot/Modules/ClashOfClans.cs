﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Discord.Commands;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Discord.Modules;
using NadekoBot.Classes.ClashOfClans;
using NadekoBot.Modules;

namespace NadekoBot.Commands {
    internal class ClashOfClans : DiscordModule
    {
        public override string Prefix { get; } = NadekoBot.Config.CommandPrefixes.ClashOfClans;

        public static ConcurrentDictionary<ulong, List<ClashWar>> ClashWars { get; } = new ConcurrentDictionary<ulong, List<ClashWar>>();

        private readonly object writeLock = new object();

        public override void Install(ModuleManager manager) {
            manager.CreateCommands("", cgb => {

                cgb.CreateCommand(Prefix + "createwar")
                    .Alias(Prefix + "cw")
                    .Description(
                        $"Creates a new war by specifying a size (>10 and multiple of 5) and enemy clan name.\n**Usage**:{Prefix}cw 15 The Enemy Clan")
                    .Parameter("size")
                    .Parameter("enemy_clan", ParameterType.Unparsed)
                    .Do(async e => {
                        if (!e.User.ServerPermissions.ManageChannels)
                            return;
                        List<ClashWar> wars;
                        if (!ClashWars.TryGetValue(e.Server.Id, out wars)) {
                            wars = new List<ClashWar>();
                            if (!ClashWars.TryAdd(e.Server.Id, wars))
                                return;
                        }
                        var enemyClan = e.GetArg("enemy_clan");
                        if (string.IsNullOrWhiteSpace(enemyClan)) {
                            return;
                        }
                        int size;
                        if (!int.TryParse(e.GetArg("size"), out size) || size < 10 || size > 50 || size % 5 != 0) {
                            await e.Channel.SendMessage("💢🔰 Not a Valid war size");
                            return;
                        }
                        var cw = new ClashWar(enemyClan, size, e);
                        //cw.Start();
                        wars.Add(cw);
                        cw.OnUserTimeExpired += async (u) => {
                            try {
                                await
                                    e.Channel.SendMessage(
                                        $"❗🔰**Claim from @{u} for a war against {cw.ShortPrint()} has expired.**");
                            } catch { }
                        };
                        cw.OnWarEnded += async () => {
                            try {
                                await e.Channel.SendMessage($"❗🔰**War against {cw.ShortPrint()} ended.**");
                            } catch { }
                        };
                        await e.Channel.SendMessage($"❗🔰**CREATED CLAN WAR AGAINST {cw.ShortPrint()}**");
                        //war with the index X started.
                    });

                cgb.CreateCommand(Prefix + "sw")
                    .Alias(Prefix + "startwar")
                    .Description("Starts a war with a given number.")
                    .Parameter("number", ParameterType.Required)
                    .Do(async e => {
                        var warsInfo = GetInfo(e);
                        if (warsInfo == null) {
                            await e.Channel.SendMessage("💢🔰 **That war does not exist.**");
                            return;
                        }
                        var war = warsInfo.Item1[warsInfo.Item2];
                        try {
                            var startTask = war.Start();
                            await e.Channel.SendMessage($"🔰**STARTED WAR AGAINST {war.ShortPrint()}**");
                            await startTask;
                        } catch {
                            await e.Channel.SendMessage($"🔰**WAR AGAINST {war.ShortPrint()} IS ALREADY STARTED**");
                        }
                    });

                cgb.CreateCommand(Prefix + "listwar")
                    .Alias(Prefix + "lw")
                    .Description($"Shows the active war claims by a number. Shows all wars in a short way if no number is specified.\n**Usage**: {Prefix}lw [war_number] or {Prefix}lw")
                    .Parameter("number", ParameterType.Optional)
                    .Do(async e => {
                        // if number is null, print all wars in a short way
                        if (string.IsNullOrWhiteSpace(e.GetArg("number"))) {
                            //check if there are any wars
                            List<ClashWar> wars = null;
                            ClashWars.TryGetValue(e.Server.Id, out wars);
                            if (wars == null || wars.Count == 0) {
                                await e.Channel.SendMessage("🔰 **No active wars.**");
                                return;
                            }

                            var sb = new StringBuilder();
                            sb.AppendLine("🔰 **LIST OF ACTIVE WARS**");
                            sb.AppendLine("**-------------------------**");
                            for (var i = 0; i < wars.Count; i++) {
                                sb.AppendLine($"**#{i + 1}.**  `Enemy:` **{wars[i].EnemyClan}**");
                                sb.AppendLine($"\t\t`Size:` **{wars[i].Size} v {wars[i].Size}**");
                                sb.AppendLine("**-------------------------**");
                            }
                            await e.Channel.SendMessage(sb.ToString());
                            return;
                        }
                        //if number is not null, print the war needed
                        var warsInfo = GetInfo(e);
                        if (warsInfo == null) {
                            await e.Channel.SendMessage("💢🔰 **That war does not exist.**");
                            return;
                        }
                        await e.Channel.SendMessage(warsInfo.Item1[warsInfo.Item2].ToString());
                    });

                cgb.CreateCommand(Prefix + "claim")
                    .Alias(Prefix + "call")
                    .Alias(Prefix + "c")
                    .Description($"Claims a certain base from a certain war. You can supply a name in the third optional argument to claim in someone else's place. \n**Usage**: {Prefix}call [war_number] [base_number] [optional_other_name]")
                    .Parameter("number")
                    .Parameter("baseNumber")
                    .Parameter("other_name", ParameterType.Unparsed)
                    .Do(async e => {
                        var warsInfo = GetInfo(e);
                        if (warsInfo == null || warsInfo.Item1.Count == 0) {
                            await e.Channel.SendMessage("💢🔰 **That war does not exist.**");
                            return;
                        }
                        int baseNum;
                        if (!int.TryParse(e.GetArg("baseNumber"), out baseNum)) {
                            await e.Channel.SendMessage("💢🔰 **Invalid base number.**");
                            return;
                        }
                        var usr =
                            string.IsNullOrWhiteSpace(e.GetArg("other_name")) ?
                            e.User.Name :
                            e.GetArg("other_name");
                        try {
                            var war = warsInfo.Item1[warsInfo.Item2];
                            war.Call(usr, baseNum - 1);
                            await e.Channel.SendMessage($"🔰**{usr}** claimed a base #{baseNum} for a war against {war.ShortPrint()}");
                        } catch (Exception ex) {
                            await e.Channel.SendMessage($"💢🔰 {ex.Message}");
                        }
                    });

                cgb.CreateCommand(Prefix + "cf")
                    .Alias(Prefix + "claimfinish")
                    .Description($"Finish your claim if you destroyed a base. Optional second argument finishes for someone else.\n**Usage**: {Prefix}cf [war_number] [optional_other_name]")
                    .Parameter("number", ParameterType.Required)
                    .Parameter("other_name", ParameterType.Unparsed)
                    .Do(async e => {
                        var warInfo = GetInfo(e);
                        if (warInfo == null || warInfo.Item1.Count == 0) {
                            await e.Channel.SendMessage("💢🔰 **That war does not exist.**");
                            return;
                        }
                        var usr =
                            string.IsNullOrWhiteSpace(e.GetArg("other_name")) ?
                            e.User.Name :
                            e.GetArg("other_name");

                        var war = warInfo.Item1[warInfo.Item2];
                        try {
                            var baseNum = war.FinishClaim(usr);
                            await e.Channel.SendMessage($"❗🔰{e.User.Mention} **DESTROYED** a base #{baseNum + 1} in a war against {war.ShortPrint()}");
                        } catch (Exception ex) {
                            await e.Channel.SendMessage($"💢🔰 {ex.Message}");
                        }
                    });

                cgb.CreateCommand(Prefix + "unclaim")
                    .Alias(Prefix + "uncall")
                    .Alias(Prefix + "uc")
                    .Description($"Removes your claim from a certain war. Optional second argument denotes a person in whos place to unclaim\n**Usage**: {Prefix}uc [war_number] [optional_other_name]")
                    .Parameter("number", ParameterType.Required)
                    .Parameter("other_name", ParameterType.Unparsed)
                    .Do(async e => {
                        var warsInfo = GetInfo(e);
                        if (warsInfo == null || warsInfo.Item1.Count == 0) {
                            await e.Channel.SendMessage("💢🔰 **That war does not exist.**");
                            return;
                        }
                        var usr =
                            string.IsNullOrWhiteSpace(e.GetArg("other_name")) ?
                            e.User.Name :
                            e.GetArg("other_name");
                        try {
                            var war = warsInfo.Item1[warsInfo.Item2];
                            var baseNumber = war.Uncall(usr);
                            await e.Channel.SendMessage($"🔰 @{usr} has **UNCLAIMED** a base #{baseNumber + 1} from a war against {war.ShortPrint()}");
                        } catch (Exception ex) {
                            await e.Channel.SendMessage($"💢🔰 {ex.Message}");
                        }
                    });

                cgb.CreateCommand(Prefix + "endwar")
                    .Alias(Prefix + "ew")
                    .Description($"Ends the war with a given index.\n**Usage**:{Prefix}ew [war_number]")
                    .Parameter("number")
                    .Do(async e => {
                        var warsInfo = GetInfo(e);
                        if (warsInfo == null) {
                            await e.Channel.SendMessage("💢🔰 That war does not exist.");
                            return;
                        }
                        warsInfo.Item1[warsInfo.Item2].End();

                        var size = warsInfo.Item1[warsInfo.Item2].Size;
                        warsInfo.Item1.RemoveAt(warsInfo.Item2);
                    });
            });
        }

        private static Tuple<List<ClashWar>, int> GetInfo(CommandEventArgs e) {
            //check if there are any wars
            List<ClashWar> wars = null;
            ClashWars.TryGetValue(e.Server.Id, out wars);
            if (wars == null || wars.Count == 0) {
                return null;
            }
            // get the number of the war
            int num;
            if (string.IsNullOrWhiteSpace(e.GetArg("number")))
                num = 0;
            else if (!int.TryParse(e.GetArg("number"), out num) || num > wars.Count) {
                return null;
            }
            num -= 1;
            //get the actual war
            return new Tuple<List<ClashWar>, int>(wars, num);
        }
    }
}
