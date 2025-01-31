﻿using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Timers;
using HarmonyLib;
using UnityEngine;
using BepInEx.Configuration;
using Heluo;
using Heluo.UI;
using Heluo.FSM;
using Heluo.FSM.Main;
using Heluo.Data;
using Heluo.Flow;
using Heluo.Battle;
using Heluo.Resource;
using Heluo.Utility;
using FileHelpers;
using Newtonsoft.Json;
using System.Reflection;
using System.Threading.Tasks;

namespace PathOfWuxia
{
    // 多Mod支持
    [System.ComponentModel.DisplayName("Mod支持")]
    [System.ComponentModel.Description("Mod支持，若要加Mod必须勾选。支持自定义语音、音乐，扩展新的特效模型包等等")]
    public class HookModSupport : IHook
    {
        static private PluginBinarizer Plugin = null;

        public void OnRegister(PluginBinarizer plugin)
        {
            Plugin = plugin;
            modBasePath = plugin.Config.Bind("Mod设置", "Mod总路径", "Mods\\", new ConfigDescription("Mod主目录", null, new ConfigurationManagerAttributes { IsAdvanced = true, Order = 3 }));
            modBattleVoicePath = Plugin.Config.Bind("Mod设置", "Mod战斗语音路径", "audio/voice/um_{0}_{1}.ogg", new ConfigDescription("可更改相对路径和扩展名", null, new ConfigurationManagerAttributes { IsAdvanced = true }));
            modTalkVoicePath = Plugin.Config.Bind("Mod设置", "Mod对话语音路径", "audio/voice/talk_{0}.ogg", new ConfigDescription("可更改相对路径和扩展名", null, new ConfigurationManagerAttributes { IsAdvanced = true }));
            string[] dirs = null;
            if (Directory.Exists(modBasePath.Value))
                dirs = Directory.GetDirectories(modBasePath.Value);
            modList = plugin.Config.Bind(
                "Mod设置", "Mod集合",
                dirs != null ? string.Join(",", from dir in dirs select (dir.Replace(modBasePath.Value, ""))) : "",
                new ConfigDescription("多Mod以逗号分隔按顺序加载，设为空或有错则不加载Mod。须重启游戏生效", null, new ConfigurationManagerAttributes { Order = 2 })
                );
        }
        static private void BindSubConfig()
        {
            modTheme = Plugin.Config.Bind("Mod设置", "Mod主菜单音乐", string.Empty, "下次进入主菜单生效");
            modCustomVoice = Plugin.Config.Bind("Mod设置", "Mod语音开关", false, "配音开关");
        }

        static ConfigEntry<string> modBasePath;
        static ConfigEntry<string> modList;
        static ConfigEntry<string> modTheme;
        static ConfigEntry<bool> modCustomVoice;
        static ConfigEntry<string> modBattleVoicePath;
        static ConfigEntry<string> modTalkVoicePath;

        // 最简单的方式：更改ExternalResourceProvider的外部路径，但无法加载音乐
        // 尝试过挂接泛型函数T Load<T>(string)，但不好用，他总会使用最后一个类的泛型注入导致其他类型无法读取，为Harmony固有问题。
        //[HarmonyPostfix, HarmonyPatch(typeof(ExternalResourceProvider), MethodType.Constructor, new Type[] { typeof(ICoroutineRunner), typeof(Heluo.Mod.IModManager) })]
        //public static void ModPatch_Constructor(ExternalResourceProvider __instance)
        //{
        //    Traverse.Create(__instance).Field("ExternalDirectory").SetValue(modPath.Value);
        //}

        // 更换 ExternalResourceProvider 为 ModResourceProvider
        [HarmonyPostfix, HarmonyPatch(typeof(ResourceManager), "Reset", new Type[] { typeof(ICoroutineRunner), typeof(Heluo.Mod.IModManager), typeof(Type[]) })]
        public static void ModPatch_Reset(ResourceManager __instance, ICoroutineRunner runner)
        {
            Console.WriteLine("ModPatch_Reset");
            string[] paths = modList.Value.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length > 0)
            {
                var provider = Traverse.Create(__instance).Field("provider").GetValue<IChainedResourceProvider>();
                var assetBundleProvider = provider.Successor.Successor;
                var modResourceProvider = new ModResourceProvider(runner, modBasePath.Value, paths);
                if (modResourceProvider.ModDirectories.Count > 0)
                {
                    // 替换原版ExternalResourceProvider
                    GlobalLib.ModResource = modResourceProvider;
                    provider.Successor = modResourceProvider;
                    modResourceProvider.Successor = assetBundleProvider;
                    BindSubConfig();
                }
                else
                {
                    // 官方目前没做外部资源，所以可直接跳过原版ExternalResourceProvider
                    provider.Successor = assetBundleProvider;
                }

                // Debug
                Console.WriteLine("当前ResourceProvider链表: ");
                while (provider != null)
                {
                    Console.WriteLine(provider.GetType().ToString());
                    provider = provider.Successor;
                }
            }
        }
        // 1 Mod支持增删表格(即多mod支持)
        [HarmonyPrefix, HarmonyPatch(typeof(DataManager), "Inital")]
        public static bool ModPatch_Inital_Mod(ref DataManager __instance)
        {
            Console.WriteLine("ModPatch_Inital_Mod start");
            if (GlobalLib.ModResource == null)
                return true;

            string path = "config/textfiles/";
            var dict = Traverse.Create(__instance).Field("dict").GetValue<IDictionary<Type, IDictionary>>();
            foreach (var keyValuePair in dict)
            {
                if (keyValuePair.Value != null && !keyValuePair.Key.HasAttribute<JsonConfig>())
                {
                    var itemType = keyValuePair.Key;
                    var itemDic = keyValuePair.Value;

                    try
                    {
                        // 替换数据 *.txt
                        foreach (var dir in GlobalLib.ModResource.ModDirectories)
                        {
                            byte[] fileData = GlobalLib.ModResource.LoadBytesFromDir(dir, path + itemType.Name + ".txt");
                            if (fileData != null)
                            {
                                Console.WriteLine($"替换数据{path + itemType.Name + ".txt"}");
                                itemDic = (Activator.CreateInstance(itemDic.GetType(), new object[]
                                {
                                    fileData
                                }) as IDictionary);
                            }
                        }
                        // 补充数据 *_modify.txt
                        foreach (var dir in GlobalLib.ModResource.ModDirectories)
                        {
                            Console.WriteLine(dir);
                            Console.WriteLine(path + itemType.Name + "_modify.txt");
                            byte[] fileDataModify = GlobalLib.ModResource.LoadBytesFromDir(dir, path + itemType.Name + "_modify.txt");
                            if (fileDataModify != null)
                            {
                                Console.WriteLine($"补充数据{path + itemType.Name + "_modify.txt"}");
                                IDictionary dicModify = (Activator.CreateInstance(itemDic.GetType(), new object[]
                                {
                                    fileDataModify
                                }) as IDictionary);
                                foreach (var key in dicModify.Keys)
                                {
                                    itemDic[key] = dicModify[key];
                                }
                            }
                        }
                        // 删除数据 *_remove.txt
                        foreach (var dir in GlobalLib.ModResource.ModDirectories)
                        {
                            byte[] fileDataRemove = GlobalLib.ModResource.LoadBytesFromDir(dir, path + itemType.Name + "_remove.txt");
                            if (fileDataRemove != null)
                            {
                                Console.WriteLine($"删除数据{path + itemType.Name + "_remove.txt"}");
                                IDictionary dicRemove = (Activator.CreateInstance(itemDic.GetType(), new object[]
                                {
                                    fileDataRemove
                                }) as IDictionary);
                                foreach (var key in dicRemove.Keys)
                                {
                                    if (itemDic.Contains(key))
                                        itemDic.Remove(key);
                                }
                            }
                        }
                    }
                    catch (ConvertException ex)
                    {
                        Console.WriteLine(string.Concat(new object[]
                        {
                            "解析Mod数据 ",
                            itemType.Name,
                            " 時發生錯誤 !!\r\n行數 : ",
                            ex.LineNumber,
                            ", 欄位 : ",
                            ex.ColumnNumber,
                            ", 類型 = ",
                            ex.FieldType.Name,
                            ", 名稱 = ",
                            ex.FieldName,
                            "\r\n",
                            ex
                        }));
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine(string.Concat(new object[]
                        {
                            "解析Mod数据 ",
                            itemType.Name,
                            " 時發生錯誤 !!\r\n",
                            ex2
                        }));
                    }
                }
            }
            Console.WriteLine("ModPatch_Inital_Mod end");
            return true;
        }

        // 2 修改主题音乐
        [HarmonyPrefix, HarmonyPatch(typeof(MusicPlayer), "ChangeMusic", new Type[] { typeof(string), typeof(float), typeof(float), typeof(bool), typeof(bool), typeof(bool) })]
        public static bool ModPatch_ChangeTheme(ref string _name)
        {
            Console.WriteLine("ModPatch_ChangeTheme");
            if (GlobalLib.ModResource != null && !modTheme.Value.IsNullOrEmpty() && _name == "In_theme_01.wav")
                _name = modTheme.Value;
            return true;
        }

        // 3 玩家自定义配音-战斗
        private static Dictionary<string, List<AudioClip>> _battleVoices = new Dictionary<string, List<AudioClip>>();
        private static System.Timers.Timer _voiceTimer;

        public static void PlayCustomizedVoice(AudioClip clip)
        {
            Console.WriteLine("PlayCustomizedVoice");
            AudioSource ss = Traverse.Create(Game.MusicPlayer).Field("single_source").GetValue<AudioSource>();
            var currVol = Traverse.Create(Game.MusicPlayer).Field("current_volume_percent");
            if (ss == null)
            {
                return;
            }
            ss.Stop();
            ss.spatialBlend = 0f;
            ss.gameObject.transform.localPosition = Vector3.zero;
            ss.volume = GameConfig.SoundVolume;
            ss.PlayOneShot(clip);
            currVol.SetValue(0.2f);
            Game.MusicPlayer.SetVolume();
            if (_voiceTimer == null)
            {
                _voiceTimer = new System.Timers.Timer();
                _voiceTimer.Elapsed += delegate (object source, ElapsedEventArgs e)
                {
                    currVol.SetValue(1f);
                    Game.MusicPlayer.SetVolume();
                };
            }
            _voiceTimer.Stop();
            _voiceTimer.Interval = (double)(clip.length * 1000f);
            _voiceTimer.Start();
        }
        public static void PlayCvByCharacter(string id)
        {
            Console.WriteLine("PlayCvByCharacter");
            List<AudioClip> list;
            if (!_battleVoices.ContainsKey(id))
            {
                list = new List<AudioClip>();
                for (int i = 0; i < 5; i++)
                {
                    AudioClip audioClip = Game.Resource.Load<AudioClip>(string.Format(modBattleVoicePath.Value, id, i));
                    if (audioClip != null)
                    {
                        list.Add(audioClip);
                    }
                }
                _battleVoices.Add(id, list);
            }
            else
            {
                list = _battleVoices[id];
            }
            if (list.Count > 0)
            {
                PlayCustomizedVoice(list[UnityEngine.Random.Range(0, list.Count)]);
            }
        }
        [HarmonyPostfix, HarmonyPatch(typeof(UIBattle), "OpenBattleStatus", new Type[] { typeof(WuxiaUnit) })]
        public static void ModPatch_BattleVoice(WuxiaUnit _unit)
        {
            Console.WriteLine("ModPatch_BattleVoice");
            if (GlobalLib.ModResource != null && modCustomVoice.Value)
            {
                PlayCvByCharacter(_unit.ExteriorId);
            }
        }
        // 4 玩家自定义配音-对话
        public static bool PlayCvByPath(string soundPath)
        {
            Console.WriteLine("PlayCvByPath");
            AudioClip audioClip = Game.Resource.Load<AudioClip>(soundPath);
            if (audioClip == null)
            {
                return false;
            }
            PlayCustomizedVoice(audioClip);
            return true;
        }
        [HarmonyPostfix, HarmonyPatch(typeof(CtrlTalk), "SetMessageView", new Type[] { typeof(Talk) })]
        public static void ModPatch_TalkVoice(Talk talk)
        {
            Console.WriteLine("ModPatch_TalkVoice");
            if (GlobalLib.ModResource != null && modCustomVoice.Value)
            {
                PlayCvByPath(string.Format(modTalkVoicePath.Value, talk.Id));
            }
        }

        // 5 进战斗无需填写场景名；可随机场景
        private static string CachedBattleId = "";
        [HarmonyPrefix, HarmonyPatch(typeof(BattleAction), "GetValue")]
        public static bool ModPatch_BattleAction(BattleAction __instance, ref bool __result)
        {
            Console.WriteLine("ModPatch_BattleAction");
            if (!Application.isPlaying || __instance.battleId.IsNullOrEmpty())
            {
                __result = false;
                return false;
            }
            string battleId = __instance.battleId;
            BattleArea battleArea = Game.Data.Get<BattleArea>(battleId);
            BattleGrid battleGrid = Game.Data.Get<BattleGrid>(battleArea?.BattleMap);
            if (battleGrid == null)
            {
                battleGrid = Randomizer.GetOneFromData<BattleGrid>(battleArea?.BattleMap);
                if (battleGrid != null)
                {
                    BattleArea battleAreaClone = battleArea.Clone<BattleArea>();
                    battleAreaClone.Id = "!" + battleAreaClone.Id;
                    battleAreaClone.BattleMap = battleGrid.Id;  // 复写mapId
                    ModExtensionSaveData.AddTempItem(battleAreaClone);
                    battleId = battleAreaClone.Id;
                }
            }
            string mapId = battleGrid?.MapId;
            Console.WriteLine("当前MapId=" + Game.GameData.MapId);
            Console.WriteLine("需要MapId=" + mapId);
            if (mapId == Game.GameData.MapId)
            {
                Game.FSM.SendEvent("BATTLE", new Heluo.FSM.Main.BattleEventArgs() { BattleId = battleId });
                __result = true;
            }
            else
            {
                CachedBattleId = battleId;
                Console.WriteLine("设置BattleId=" + CachedBattleId);
                Game.FSM.SendEvent("LOADING", new LoadingEventArgs
                {
                    MapId = mapId,
                    CinematicId = null,
                    TimeStage = Heluo.Manager.TimeStage.None,
                    LoadType = LoadType.Default
                });
                __result = true;
            }
            return false;
        }
        [HarmonyPrefix, HarmonyPatch(typeof(GameState<MainStateMachine>), "SendEvent", new Type[] { typeof(string), typeof(EventArgs) })]
        public static bool ModPatch_LoadBattlePost(GameState<MainStateMachine> __instance, ref string eventName, ref EventArgs e)
        {
            Console.WriteLine("ModPatch_LoadBattlePost");
            Console.WriteLine("当前BattleId=" + CachedBattleId);
            Console.WriteLine("eventName=" + eventName);
            Console.WriteLine("e=" + e);
            Console.WriteLine("当前MapId=" + Game.GameData.MapId);
            if (!CachedBattleId.IsNullOrEmpty() && eventName == "CINEMATIC" && e == null)
            {
                eventName = "BATTLE";
                e = new Heluo.FSM.Main.BattleEventArgs() { BattleId = CachedBattleId };
                CachedBattleId = "";
            }
            return true;
        }

        // 6 加载其他路径的AssetBundle
        [HarmonyPrefix, HarmonyPatch(typeof(AssetBundleSheet), "GetBundleName", new Type[] { typeof(string) })]
        public static bool ModPatch_AssetBundle(AssetBundleSheet __instance, string filePath, ref string __result)
        {
            //Console.WriteLine("ModPatch_AssetBundle");
            string text = filePath.ToLower().Trim();
            if (__instance.FilesInfo.ContainsKey(text))
            {
                __result = __instance.BundleList[__instance.FilesInfo[text]].AssetBundleName;
            }
            else
            {
                Console.WriteLine("沒有包到這個路徑的檔案:" + text);
                __result = null;
            }
            return false;
        }

        //修复了召唤非小熊猫角色时模型变大1.5倍的bug
        [HarmonyPostfix, HarmonyPatch(typeof(SummonProcessStrategy), "Process")]
        public static async Task SummonProcessStrategyPatch_Process(Task task, SummonProcessStrategy __instance, Heluo.Battle.BattleEventArgs arg)
        {
            Console.WriteLine("SummonProcessStrategyPatch_Process start");
            await task;
            WuxiaUnit summonUnit = Traverse.Create(__instance).Field("summonUnit").GetValue<WuxiaUnit>();
            if (summonUnit.UnitID != "in91001" && summonUnit.UnitID != "in91001_1" && summonUnit.UnitID != "in91002" && summonUnit.UnitID != "in91002_2")
            {
                summonUnit.transform.GetChild(0).localScale = new Vector3(1.0f, 1.0f, 1.0f);
            }
            Console.WriteLine("SummonProcessStrategyPatch_Process end");
        }
    }
}
