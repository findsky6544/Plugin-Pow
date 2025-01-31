﻿using HarmonyLib;
using BepInEx.Configuration;
using Heluo;
using Heluo.UI;
using UnityEngine;
using UnityEngine.UI;
using System.ComponentModel;

namespace PathOfWuxia
{
    [System.ComponentModel.DisplayName("战斗时显示名字")]
    [Description("战斗时显示角色名字")]
    public class HookBattleName : IHook
    {
        static ConfigEntry<Vector3> elementTextPos;
        static ConfigEntry<bool> elementKey;
        static bool elementShow = false;
        public void OnRegister(PluginBinarizer plugin)
        {
            elementTextPos = plugin.Config.Bind("界面改进", "名字位置", new Vector3(8, 18, 0), "调整名字位置");
            elementKey = plugin.Config.Bind("界面改进", "名字显示", false, "战斗时显示名字。调整位置后需开关一次生效");

            plugin.onUpdate += OnUpdate;
        }

        void OnUpdate()
        {
            if (elementShow != elementKey.Value)
            {
                elementShow = elementKey.Value;
                foreach (var wgbar in GameObject.FindObjectsOfType<WgBar>())
                {
                    ProcessElementDisplay(wgbar);
                }
            }
        }
        public static void ProcessElementDisplay(WgBar wgbar)
        {
            Text name;
            Slider hp = Traverse.Create(wgbar).Field("hp").GetValue<Slider>();
            var trans2 = hp.transform.Find("UnitName");
            if (trans2 == null && wgbar.Unit != null)
            {
                GameObject gameObject2 = new GameObject("UnitName");
                gameObject2.transform.SetParent(hp.transform, false);
                name = gameObject2.AddComponent<Text>();
                name.text = wgbar.Unit.FullName;
                name.font = Game.Resource.Load<Font>("Assets/Font/kaiu.ttf");
                name.fontSize = 18;
                name.fontStyle = FontStyle.Bold;
                name.alignment = TextAnchor.MiddleLeft;
                name.rectTransform.sizeDelta = new Vector2(120f, 20f);
                name.transform.localPosition = elementTextPos.Value;
            }
            else
            {
                name = trans2.gameObject.GetComponent<Text>();
            }
            if (elementShow)
            {
                name.gameObject.SetActive(true);
                name.transform.localPosition = elementTextPos.Value;
            }
            else
            {
                name.gameObject.SetActive(false);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(WgBar), "Unit", MethodType.Setter)]
        public static void UnitElementPatch(WgBar __instance)
        {
            ProcessElementDisplay(__instance);
        }
    }
}
