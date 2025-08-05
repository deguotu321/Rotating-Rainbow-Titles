using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using CommandSystem;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Interfaces;
using Exiled.Loader;
using Exiled.Permissions.Extensions;
using RemoteAdmin;
using UnityEngine;
using YamlDotNet.Serialization;

namespace NicknamePlugin
{
    public class NicknamePlugin : Plugin<NicknamePlugin.PluginConfig>
    {
        public override string Author => "freefish";
        public override string Name => "昵称插件";
        public override string Prefix => "nickname";
        public override Version Version => new Version(1, 1, 1); // 更新版本号
        public override Version RequiredExiledVersion => new Version(9, 6, 1);

        public static NicknamePlugin Instance { get; private set; }
        public Dictionary<string, PlayerTagData> PlayerTags { get; private set; } = new Dictionary<string, PlayerTagData>();
        private Dictionary<string, PlayerTagData> TemporaryTags { get; set; } = new Dictionary<string, PlayerTagData>();

        private string dataFilePath;
        private Coroutine updateCoroutine;
        private GameObject coroutineHolder;

        public class PluginConfig : IConfig
        {
            [Description("是否启用插件")]
            public bool IsEnabled { get; set; } = true;

            [Description("调试模式")]
            public bool Debug { get; set; } = false;

            [Description("彩虹色循环使用的颜色名称列表")]
            public List<string> RainbowColorNames { get; set; } = new List<string>
            {
                "pink",
                "red",
                "crimson",
                "tomato",
                "orange",
                "gold",
                "lime",
                "light_green",
                "emerald",
                "teal",
                "aqua",
                "cyan",
                "blue",
                "purple",
                "deep_pink",
                "magenta"
            };

            [Description("所有可用颜色名称及其十六进制值")]
            public Dictionary<string, string> AvailableColors { get; set; } = new Dictionary<string, string>
            {
                {"pink", "#FF96DE"},
                {"red", "#C50000"},
                {"brown", "#944710"},
                {"silver", "#A0A0A0"},
                {"light_green", "#32CD32"},
                {"crimson", "#DC143C"},
                {"cyan", "#0087EB"},
                {"aqua", "#00FFFF"},
                {"deep_pink", "#FF1493"},
                {"tomato", "#FF6448"},
                {"yellow", "yellow"},
                {"magenta", "#FF0090"},
                {"blue_green", "#ADFFB8"},
                {"orange", "#FF9966"},
                {"lime", "#BFFF00"},
                {"green", "#228B22"},
                {"emerald", "#50C878"},
                {"carmine", "#960018"},
                {"nickel", "#727472"},
                {"mint", "#98F888"},
                {"army_green", "#4B5320"},
                {"pumpkin", "#EE7600"},
                {"gold", "#EFC01A"},
                {"teal", "#008080"},
                {"blue", "#005EBC"},
                {"purple", "#8137CE"},
                {"light_red", "#FD8272"},
                {"silver_blue", "#666699"},
                {"police_blue", "#002DB3"},
                {"white", "white"},
                {"black", "black"}
            };

            [Description("数据存储文件夹路径")]
            public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EXILED", "Configs");
        }

        public class PlayerTagData
        {
            public string Tag { get; set; }
            public string ColorName { get; set; }
            public bool IsRainbow { get; set; }
            public int CurrentColorIndex { get; set; }
            public bool IsCarousel { get; set; }
            public int CarouselInterval { get; set; }
            public List<string> CarouselTags { get; set; }
            public int CurrentCarouselIndex { get; set; }
            public float LastCarouselUpdateTime { get; set; }
            public bool IsPermanent { get; set; } = true;
        }

        public override void OnEnabled()
        {
            Instance = this;
            dataFilePath = Path.Combine(Config.Folder, "playertags.yml");

            if (!Directory.Exists(Config.Folder))
                Directory.CreateDirectory(Config.Folder);

            LoadData();

            Exiled.Events.Handlers.Player.Verified += OnPlayerVerified;
            Exiled.Events.Handlers.Player.ChangingGroup += OnChangingGroup;
            Exiled.Events.Handlers.Server.WaitingForPlayers += OnWaitingForPlayers;
            Exiled.Events.Handlers.Server.RoundEnded += OnRoundEnded;

            // 创建协程持有对象
            coroutineHolder = new GameObject("NicknamePlugin_CoroutineHolder");
            UnityEngine.Object.DontDestroyOnLoad(coroutineHolder);

            // 立即启动协程
            if (coroutineHolder != null)
            {
                updateCoroutine = coroutineHolder.AddComponent<MonoBehaviour>().StartCoroutine(UpdateTags());
            }

            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            Exiled.Events.Handlers.Player.Verified -= OnPlayerVerified;
            Exiled.Events.Handlers.Player.ChangingGroup -= OnChangingGroup;
            Exiled.Events.Handlers.Server.WaitingForPlayers -= OnWaitingForPlayers;
            Exiled.Events.Handlers.Server.RoundEnded -= OnRoundEnded;

            if (updateCoroutine != null && coroutineHolder != null)
                coroutineHolder.GetComponent<MonoBehaviour>().StopCoroutine(updateCoroutine);

            if (coroutineHolder != null)
                UnityEngine.Object.Destroy(coroutineHolder);

            SaveData();

            base.OnDisabled();
        }

        private void OnRoundEnded(Exiled.Events.EventArgs.Server.RoundEndedEventArgs ev)
        {
            // 清除临时头衔
            TemporaryTags.Clear();
        }

        private void OnChangingGroup(Exiled.Events.EventArgs.Player.ChangingGroupEventArgs ev)
        {
            // 当玩家组改变时重新应用头衔
            ApplyPlayerTag(ev.Player);
        }

        private void OnWaitingForPlayers()
        {
            // 确保协程运行
            if (coroutineHolder != null && updateCoroutine == null)
            {
                updateCoroutine = coroutineHolder.AddComponent<MonoBehaviour>().StartCoroutine(UpdateTags());
            }
        }

        private IEnumerator UpdateTags()
        {
            while (true)
            {
                yield return new WaitForSeconds(0.1f);
                float currentTime = Time.time;

                foreach (var player in Player.List)
                {
                    try
                    {
                        PlayerTagData tagData = GetPlayerTagData(player.UserId);
                        if (tagData != null)
                        {
                            UpdatePlayerTag(player, tagData, currentTime);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"更新玩家 {player.Nickname} 的头衔时出错: {e}");
                    }
                }
            }
        }

        private void OnPlayerVerified(Exiled.Events.EventArgs.Player.VerifiedEventArgs ev)
        {
            ApplyPlayerTag(ev.Player);
        }

        private PlayerTagData GetPlayerTagData(string userId)
        {
            if (PlayerTags.TryGetValue(userId, out var permanentTag) && permanentTag != null)
                return permanentTag;

            if (TemporaryTags.TryGetValue(userId, out var tempTag) && tempTag != null)
                return tempTag;

            return null;
        }

        public void ApplyPlayerTag(Player player)
        {
            PlayerTagData tagData = GetPlayerTagData(player.UserId);
            if (tagData != null)
            {
                UpdatePlayerTag(player, tagData, Time.time);
            }
        }

        public void UpdatePlayerTag(Player player, PlayerTagData data, float currentTime)
        {
            try
            {
                string tag = data.Tag;
                string colorHex = GetColorHex(data.ColorName);

                // 处理彩虹色
                if (data.IsRainbow)
                {
                    colorHex = GetColorHex(Config.RainbowColorNames[data.CurrentColorIndex]);
                    data.CurrentColorIndex = (data.CurrentColorIndex + 1) % Config.RainbowColorNames.Count;
                }

                // 处理轮播头衔
                if (data.IsCarousel && data.CarouselTags != null && data.CarouselTags.Count > 0)
                {
                    // 计算时间差（秒）
                    float timeSinceLastUpdate = currentTime - data.LastCarouselUpdateTime;
                    float intervalSeconds = data.CarouselInterval / 1000f;

                    if (timeSinceLastUpdate >= intervalSeconds)
                    {
                        data.CurrentCarouselIndex = (data.CurrentCarouselIndex + 1) % data.CarouselTags.Count;
                        data.LastCarouselUpdateTime = currentTime;

                        if (Config.Debug)
                        {
                            Log.Debug($"[轮播] 玩家 {player.Nickname} 更新头衔索引: {data.CurrentCarouselIndex}");
                        }
                    }
                    tag = data.CarouselTags[data.CurrentCarouselIndex];
                }

                // 设置玩家头衔
                player.RankName = tag;
                player.RankColor = colorHex;

                if (Config.Debug)
                {
                    Log.Debug($"设置 {player.Nickname} 的头衔: [{colorHex}]{tag}");
                }
            }
            catch (Exception e)
            {
                Log.Error($"更新 {player.Nickname} 的头衔时出错: {e}");
            }
        }

        public string GetColorHex(string colorNameOrHex)
        {
            if (string.IsNullOrEmpty(colorNameOrHex))
                return "white";

            // 如果是十六进制值直接返回
            if (colorNameOrHex.StartsWith("#") && colorNameOrHex.Length >= 4)
                return colorNameOrHex;

            // 从可用颜色中查找
            string lowerColor = colorNameOrHex.ToLower();
            if (Config.AvailableColors.TryGetValue(lowerColor, out string hexColor))
            {
                return hexColor;
            }

            // 尝试直接使用颜色名称
            return colorNameOrHex;
        }

        private void LoadData()
        {
            if (!File.Exists(dataFilePath))
            {
                PlayerTags = new Dictionary<string, PlayerTagData>();
                return;
            }

            try
            {
                string yaml = File.ReadAllText(dataFilePath);
                PlayerTags = Loader.Deserializer.Deserialize<Dictionary<string, PlayerTagData>>(yaml) ??
                                  new Dictionary<string, PlayerTagData>();

                Log.Info($"已加载 {PlayerTags.Count} 个玩家的头衔数据");
            }
            catch (Exception e)
            {
                Log.Error($"加载玩家头衔数据失败: {e}");
                PlayerTags = new Dictionary<string, PlayerTagData>();
            }
        }

        public void SaveData()
        {
            try
            {
                string yaml = Loader.Serializer.Serialize(PlayerTags);
                File.WriteAllText(dataFilePath, yaml);
                Log.Info($"已保存 {PlayerTags.Count} 个玩家的头衔数据");
            }
            catch (Exception e)
            {
                Log.Error($"保存玩家头衔数据失败: {e}");
            }
        }

        public bool SetPlayerTag(string userId, string tag, string colorNameOrHex, bool isRainbow, bool isPermanent, bool isCarousel = false, int carouselInterval = 0)
        {
            try
            {
                PlayerTagData tagData = new PlayerTagData
                {
                    Tag = tag,
                    ColorName = colorNameOrHex,
                    IsRainbow = isRainbow,
                    CurrentColorIndex = 0,
                    IsCarousel = isCarousel,
                    CarouselInterval = carouselInterval,
                    IsPermanent = isPermanent
                };

                if (isCarousel)
                {
                    // 分割轮播头衔 (使用小写x作为分隔符)
                    tagData.CarouselTags = new List<string>(tag.Split('x'));
                    tagData.CurrentCarouselIndex = 0;
                    tagData.LastCarouselUpdateTime = Time.time;

                    if (Config.Debug)
                    {
                        Log.Debug($"[轮播] 设置玩家 {userId} 的轮播头衔: {string.Join(", ", tagData.CarouselTags)}");
                    }
                }

                if (isPermanent)
                {
                    PlayerTags[userId] = tagData;
                    SaveData();
                }
                else
                {
                    TemporaryTags[userId] = tagData;
                }

                var player = Player.Get(userId);
                if (player != null)
                {
                    ApplyPlayerTag(player);
                }

                return true;
            }
            catch (Exception e)
            {
                Log.Error($"设置玩家头衔时出错: {e}");
                return false;
            }
        }

        public bool RemovePlayerTag(string userId)
        {
            bool removed = false;

            if (PlayerTags.ContainsKey(userId))
            {
                PlayerTags.Remove(userId);
                SaveData();
                removed = true;
            }

            if (TemporaryTags.ContainsKey(userId))
            {
                TemporaryTags.Remove(userId);
                removed = true;
            }

            if (removed)
            {
                var player = Player.Get(userId);
                if (player != null)
                {
                    player.RankName = null;
                    player.RankColor = "default";
                }
            }

            return removed;
        }

        [CommandHandler(typeof(RemoteAdminCommandHandler))]
        [CommandHandler(typeof(GameConsoleCommandHandler))]
        public class TagsCommand : ICommand
        {
            public string Command => "tags";
            public string[] Aliases => Array.Empty<string>();
            public string Description => "设置玩家头衔（永久/临时）";

            public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
            {
                if (arguments.Count < 4)
                {
                    response = "用法: tags <steam64位ID> <颜色/rainbow> <头衔> <是否为永久(true/false)>";
                    return false;
                }

                string steamId = arguments.At(0);
                string colorArg = arguments.At(1);
                string tag = arguments.At(2);
                string permanentArg = arguments.At(3);

                bool isRainbow = colorArg.Equals("rainbow", StringComparison.OrdinalIgnoreCase);
                string color = isRainbow ? "white" : colorArg;

                if (!bool.TryParse(permanentArg, out bool isPermanent))
                {
                    response = "是否为永久参数必须是 true 或 false";
                    return false;
                }

                Player player = Player.Get(steamId);
                if (player == null)
                {
                    response = $"找不到SteamID为 {steamId} 的玩家";
                    return false;
                }

                if (NicknamePlugin.Instance.SetPlayerTag(player.UserId, tag, color, isRainbow, isPermanent))
                {
                    response = $"已为 {player.Nickname} 设置{(isPermanent ? "永久" : "临时")}头衔: {(isRainbow ? "彩虹" : color)} {tag}";
                    return true;
                }

                response = $"无法为 {player.Nickname} 设置头衔";
                return false;
            }
        }

        [CommandHandler(typeof(RemoteAdminCommandHandler))]
        [CommandHandler(typeof(GameConsoleCommandHandler))]
        public class CTagsCommand : ICommand
        {
            public string Command => "ctags";
            public string[] Aliases => Array.Empty<string>();
            public string Description => "设置轮播头衔（永久/临时）";

            public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
            {
                if (arguments.Count < 5)
                {
                    response = "用法: ctags <steam64位ID> <颜色/rainbow> <头衔(x分隔)> <轮播毫秒> <是否为永久(true/false)>";
                    return false;
                }

                string steamId = arguments.At(0);
                string colorArg = arguments.At(1);
                string tag = arguments.At(2);
                string intervalArg = arguments.At(3);
                string permanentArg = arguments.At(4);

                bool isRainbow = colorArg.Equals("rainbow", StringComparison.OrdinalIgnoreCase);
                string color = isRainbow ? "white" : colorArg;

                if (!int.TryParse(intervalArg, out int interval) || interval <= 0)
                {
                    response = "轮播间隔必须是正整数";
                    return false;
                }

                if (!bool.TryParse(permanentArg, out bool isPermanent))
                {
                    response = "是否为永久参数必须是 true 或 false";
                    return false;
                }

                Player player = Player.Get(steamId);
                if (player == null)
                {
                    response = $"找不到SteamID为 {steamId} 的玩家";
                    return false;
                }

                if (NicknamePlugin.Instance.SetPlayerTag(player.UserId, tag, color, isRainbow, isPermanent, true, interval))
                {
                    response = $"已为 {player.Nickname} 设置{(isPermanent ? "永久" : "临时")}轮播头衔: 每 {interval} 毫秒轮播";
                    return true;
                }

                response = $"无法为 {player.Nickname} 设置轮播头衔";
                return false;
            }
        }

        [CommandHandler(typeof(RemoteAdminCommandHandler))]
        [CommandHandler(typeof(GameConsoleCommandHandler))]
        public class TagsFallCommand : ICommand
        {
            public string Command => "tagsfall";
            public string[] Aliases => Array.Empty<string>();
            public string Description => "移除所有玩家的头衔";

            public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
            {
                int count = 0;
                var userIds = NicknamePlugin.Instance.PlayerTags.Keys.ToList();

                foreach (var userId in userIds)
                {
                    var player = Player.Get(userId);
                    if (player != null)
                    {
                        player.RankName = null;
                        player.RankColor = "default";
                    }
                    count++;
                }

                NicknamePlugin.Instance.PlayerTags.Clear();
                NicknamePlugin.Instance.TemporaryTags.Clear();
                NicknamePlugin.Instance.SaveData();

                response = $"已移除所有玩家的头衔 (共 {count} 名玩家)";
                return true;
            }
        }

        [CommandHandler(typeof(RemoteAdminCommandHandler))]
        [CommandHandler(typeof(GameConsoleCommandHandler))]
        public class SetTagCommand : ICommand
        {
            public string Command => "settag";
            public string[] Aliases => new[] { "st" };
            public string Description => "设置玩家的彩色头衔";

            public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
            {
                if (sender is PlayerCommandSender playerSender && !playerSender.CheckPermission("tag.set"))
                {
                    response = "你没有权限使用此命令";
                    return false;
                }

                if (arguments.Count < 2)
                {
                    response = "用法: settag <玩家ID/名称> <头衔> [颜色/rainbow]\n" +
                             "可用颜色: " + string.Join(", ", NicknamePlugin.Instance.Config.AvailableColors.Keys.OrderBy(x => x));
                    return false;
                }

                Player player = Player.Get(arguments.At(0));
                if (player == null)
                {
                    response = "找不到该玩家";
                    return false;
                }

                string tag = arguments.At(1);
                bool isRainbow = arguments.Count > 2 && arguments.At(2).Equals("rainbow", StringComparison.OrdinalIgnoreCase);

                string color = "white";
                if (arguments.Count > 2 && !isRainbow)
                {
                    color = arguments.At(2);
                }

                if (NicknamePlugin.Instance.SetPlayerTag(player.UserId, tag, color, isRainbow, true))
                {
                    response = $"已为 {player.Nickname} 设置{(isRainbow ? "彩虹" : color)}头衔: {tag}";
                    return true;
                }
                else
                {
                    response = $"设置头衔失败，请检查服务器日志";
                    return false;
                }
            }
        }

        [CommandHandler(typeof(RemoteAdminCommandHandler))]
        [CommandHandler(typeof(GameConsoleCommandHandler))]
        public class RemoveTagCommand : ICommand
        {
            public string Command => "removetag";
            public string[] Aliases => new[] { "rt" };
            public string Description => "移除玩家的头衔";

            public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
            {
                if (sender is PlayerCommandSender playerSender && !playerSender.CheckPermission("tag.remove"))
                {
                    response = "你没有权限使用此命令";
                    return false;
                }

                if (arguments.Count < 1)
                {
                    response = "用法: removetag <玩家ID/名称>";
                    return false;
                }

                Player player = Player.Get(arguments.At(0));
                if (player == null)
                {
                    response = "找不到该玩家";
                    return false;
                }

                if (NicknamePlugin.Instance.RemovePlayerTag(player.UserId))
                {
                    response = $"已移除 {player.Nickname} 的头衔";
                    return true;
                }
                else
                {
                    response = $"{player.Nickname} 未设置头衔";
                    return false;
                }
            }
        }

        [CommandHandler(typeof(RemoteAdminCommandHandler))]
        [CommandHandler(typeof(GameConsoleCommandHandler))]
        public class TagColorsCommand : ICommand
        {
            public string Command => "tagcolors";
            public string[] Aliases => new[] { "tcolors", "tcl" };
            public string Description => "显示可用颜色列表";

            public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
            {
                var colors = NicknamePlugin.Instance.Config.AvailableColors
                    .OrderBy(x => x.Key)
                    .Select(x => $"{x.Key} -> {x.Value}");

                response = "可用颜色:\n" + string.Join("\n", colors);
                return true;
            }
        }
    }

    // 辅助类用于协程管理
    public class MonoBehaviour : UnityEngine.MonoBehaviour
    {
    }
}