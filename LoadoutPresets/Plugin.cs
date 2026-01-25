using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LoadoutPresets
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ConfigFile Cfg = null!;
        internal static ManualLogSource Log = null!;
        internal static ConfigEntry<bool> Enabled = null!;

        internal const string DEFAULTPRESET = "DEFAULT";

        private void Awake()
        {
            Cfg = Config;
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Enable / disable the mod.");
            new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();
        }

        private void OnGUI()
        {
            if (!Enabled.Value) return;
            PresetMenuUI.Draw();
        }
    }

    internal static class MenuRefs
    {
        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, Slider> FuelLevel =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, Slider>("fuelLevel");

        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, TMP_Dropdown> LiveryDropdown =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, TMP_Dropdown>("liveryDropdown");

        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, List<ValueTuple<LiveryKey, string>>> LiveryOptions =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, List<ValueTuple<LiveryKey, string>>>("liveryOptions");

        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, Aircraft> PreviewAircraft =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, Aircraft>("previewAircraft");
    }

    internal static class PresetIO
    {
        private const string ActiveKey = "ActivePreset";
        private const string SavedKey = "Saved";
        private const string FuelKey = "Fuel";
        private const string LiveryKey = "Livery";

        private static string HpKey(int i) => $"Hardpoint_{i}";

        private static string Norm(string preset)
        {
            preset = Regex.Replace((preset ?? "").Trim(), @"[=\r\n\t\\\""'\[\]]", "_");
            return preset.Length == 0 ? Plugin.DEFAULTPRESET : preset;
        }

        private static ConfigEntry<T> Entry<T>(string section, string key, T def) =>
            Plugin.Cfg.Bind(section, key, def);

        private static T Get<T>(string section, string key, T def) => Entry(section, key, def).Value;

        private static void Set<T>(string section, string key, T def, T value) => Entry(section, key, def).Value = value;

        internal static string BaseSection(AircraftDefinition def) => $"Aircraft:{def.unitName}";

        internal static string PresetSection(AircraftDefinition def, string preset) =>
            $"{BaseSection(def)}:{Norm(preset)}";

        internal static string GetActivePreset(AircraftDefinition def) =>
            Norm(Get(BaseSection(def), ActiveKey, Plugin.DEFAULTPRESET));

        internal static void SetActivePreset(AircraftDefinition def, string preset) =>
            Set(BaseSection(def), ActiveKey, Plugin.DEFAULTPRESET, Norm(preset));

        internal static bool IsSaved(AircraftDefinition def, string preset) =>
            Get(PresetSection(def, preset), SavedKey, false);

        internal static void SaveCurrentToPreset(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            if (string.IsNullOrWhiteSpace(preset)) return;

            string section = PresetSection(def, preset);
            Set(section, SavedKey, false, true);

            Set(section, FuelKey, 1f, Mathf.Clamp01(MenuRefs.FuelLevel(menu).value));

            string liveryStr = "";
            TMP_Dropdown dd = MenuRefs.LiveryDropdown(menu);
            List<ValueTuple<LiveryKey, string>> opts = MenuRefs.LiveryOptions(menu);
            if (dd != null && opts != null && (uint)dd.value < (uint)opts.Count)
                liveryStr = opts[dd.value].Item1.ToString();

            Set(section, LiveryKey, "", liveryStr);

            for (int i = 0; i < menu.weaponSelectors.Count; i++)
            {
                WeaponMount mount = menu.weaponSelectors[i].GetValue();
                Set(section, HpKey(i), "", mount?.jsonKey ?? "");
            }
            Plugin.Cfg.Save();
        }

        internal static bool LoadPreset(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            preset = Norm(preset);

            bool applied = ApplyPreset(menu, def, preset);
            SetActivePreset(def, preset);

            Plugin.Cfg.Save();
            Plugin.Log.LogInfo($"[Presets] Loaded {preset}:{def.unitName} {(applied ? "" : "(no saved preset yet)")} ");

            return applied;
        }

        internal static bool ApplyPreset(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            preset = preset?.Trim();
            if (string.IsNullOrWhiteSpace(preset)) return false;

            string section = PresetSection(def, preset);
            if (!Get(section, SavedKey, false)) return false;

            Aircraft preview = MenuRefs.PreviewAircraft(menu);
            HardpointSet[] sets = preview?.weaponManager?.hardpointSets;
            if (sets == null) return false;

            int n = Math.Min(menu.weaponSelectors.Count, sets.Length);
            for (int i = 0; i < n; i++)
            {
                string key = Get(section, HpKey(i), "") ?? "";
                menu.weaponSelectors[i].SetValue(key.Length == 0 ? null : sets[i].weaponOptions.Find(w => w != null && w.jsonKey == key));
            }
            menu.UpdateWeapons(true);

            string wantLiveryKey = Get(section, LiveryKey, "") ?? "";
            if (wantLiveryKey.Length != 0)
            {
                TMP_Dropdown dd = MenuRefs.LiveryDropdown(menu);
                List<ValueTuple<LiveryKey, string>> opts = MenuRefs.LiveryOptions(menu);

                if (dd != null && opts != null)
                {
                    int idx = opts.FindIndex(o => o.Item1.ToString() == wantLiveryKey);

                    if (idx >= 0)
                    {
                        dd.SetValueWithoutNotify(idx);
                        menu.SelectLivery();
                    }
                }
            }

            float fuel = Get(section, FuelKey, 1f);
            MenuRefs.FuelLevel(menu).SetValueWithoutNotify(Mathf.Clamp01(fuel));
            menu.ChangeFuelLevel();

            return true;
        }

        internal static void DeletePreset(AircraftDefinition def, string preset)
        {
            preset = preset?.Trim();
            if (string.IsNullOrWhiteSpace(preset) || preset == Plugin.DEFAULTPRESET)
                return;

            string section = PresetSection(def, preset);
            foreach (var k in Plugin.Cfg.Keys.ToArray())
            {
                if (string.Equals(k.Section, section, StringComparison.Ordinal))
                    Plugin.Cfg.Remove(k);
            }

            if (string.Equals(GetActivePreset(def), preset, StringComparison.Ordinal))
                SetActivePreset(def, Plugin.DEFAULTPRESET);

            Plugin.Cfg.Save();
            Plugin.Cfg.Reload();
        }

        internal static List<string> ListPresets(AircraftDefinition def)
        {
            HashSet<string> names = new(StringComparer.Ordinal) { Plugin.DEFAULTPRESET };
            try
            {
                string path = Plugin.Cfg.ConfigFilePath;
                if (File.Exists(path))
                {
                    string prefix = $"[{BaseSection(def)}:";
                    foreach (string line in File.ReadLines(path))
                    {
                        if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        if (!line.EndsWith("]", StringComparison.Ordinal)) continue;

                        string name = line.Substring(prefix.Length, line.Length - prefix.Length - 1).Trim();
                        if (name.Length != 0) names.Add(name);
                    }
                }
            }
            catch { /* ignore */ }

            List<string> list = new() { Plugin.DEFAULTPRESET };
            List<string> rest = new();

            foreach (string p in names)
            {
                if (p == Plugin.DEFAULTPRESET) continue;
                if (IsSaved(def, p)) rest.Add(p);
            }

            rest.Sort(StringComparer.Ordinal);
            list.AddRange(rest);
            return list;
        }
    }
    [HarmonyPatch(typeof(AircraftSelectionMenu), nameof(AircraftSelectionMenu.SaveDefaults))]
    internal static class Patch_SaveDefaults
    {
        static void Postfix(AircraftSelectionMenu __instance)
        {
            if (!Plugin.Enabled.Value) return;
            try
            {
                PresetMenuUI.Attach(__instance);
                AircraftDefinition def = __instance.GetSelectedType();
                PresetIO.SaveCurrentToPreset(__instance, def, Plugin.DEFAULTPRESET);
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }

    [HarmonyPatch(typeof(AircraftSelectionMenu), "LoadDefaults")]
    internal static class Patch_LoadDefaults
    {
        static void Postfix(AircraftSelectionMenu __instance)
        {
            if (!Plugin.Enabled.Value) return;
            try
            {
                PresetMenuUI.Attach(__instance);
                AircraftDefinition def = __instance.GetSelectedType();
                PresetIO.LoadPreset(__instance, def, Plugin.DEFAULTPRESET);
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }
    internal static class PresetMenuUI
    {
        internal static AircraftSelectionMenu Menu;
        internal static bool Dirty;

        private static bool _edit;
        private static string _selected = "";
        private static string _name = "";
        private static bool _confirmDelete;

        private static Rect _rect = new(10, 10, 220, 200);
        private static float _desiredH;
        private static string _cachedUnitName = "";
        private static List<string> _presets = [];

        internal static void Attach(AircraftSelectionMenu menu)
        {
            Menu = menu;
            Dirty = true;
        }

        internal static void Draw()
        {
            AircraftSelectionMenu menu = Menu;
            if (menu == null || !menu.isActiveAndEnabled) return;

            AircraftDefinition def = menu.GetSelectedType();
            if (def == null) return;

            if (Dirty || _cachedUnitName != def.unitName)
            {
                _cachedUnitName = def.unitName;
                _presets = PresetIO.ListPresets(def);
                Dirty = false;

                string active = PresetIO.GetActivePreset(def);
                string wanted = !string.IsNullOrWhiteSpace(_selected) ? _selected : active;

                int idx = _presets.IndexOf(wanted);
                if (idx < 0) idx = 0;

                _selected = _presets.Count > 0 ? _presets[Mathf.Clamp(idx, 0, _presets.Count - 1)] : "";
                if (!_edit) _name = _selected;
            }

            _rect.height = _desiredH;
            _rect.y = Screen.height * 0.2f;

            _rect.width = _edit ? 320f : 180f;
            _rect.x = Screen.width - _rect.width;

            _rect = GUI.Window(2082, _rect, _ => Window(menu, def), "Loadout Presets");
        }
        
        private static void Window(AircraftSelectionMenu menu, AircraftDefinition def)
        {
            string active = PresetIO.GetActivePreset(def);
            string focus = string.IsNullOrWhiteSpace(_selected) ? active : _selected;
            GUILayout.BeginVertical();

            for (int i = 0; i < _presets.Count; i++)
            {
                string p = _presets[i];
                bool isFocus = string.Equals(p, focus, StringComparison.Ordinal);
                GUIStyle style = isFocus ? GUI.skin.label : GUI.skin.button;

                if (GUILayout.Button(p, style))
                {
                    PresetIO.LoadPreset(menu, def, p);
                    _selected = _name = focus = p;
                    _confirmDelete = false;
                }
            }

            GUILayout.Space(6);

            if (!_edit)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Edit", GUILayout.Width(70f)))
                {
                    _edit = true;
                    _selected = _name = active;
                    _confirmDelete = false;
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Name:", GUILayout.ExpandWidth(false));
                var prevName = _name;
                _name = GUILayout.TextField(_name ?? "", 32, GUILayout.ExpandWidth(true));
                if (_confirmDelete && prevName != _name) _confirmDelete = false;
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();

                if (GUILayout.Button("New"))
                {
                    string name = (_name ?? "").Trim();
                    if (name.Length != 0 && name != Plugin.DEFAULTPRESET)
                    {
                        PresetIO.SetActivePreset(def, name);
                        PresetIO.SaveCurrentToPreset(menu, def, name);

                        _selected = _name = name;
                        _confirmDelete = false;
                        Dirty = true;

                        Plugin.Log.LogInfo($"[Presets] Saved (new) {def.unitName} / {name}");
                    }
                }

                GUI.enabled = !string.IsNullOrWhiteSpace(_selected) && _selected != Plugin.DEFAULTPRESET;

                if (GUILayout.Button("Apply"))
                {
                    string current = (_selected ?? "").Trim();
                    string requested = (_name ?? "").Trim();
                    string target = requested.Length != 0 ? requested : current;

                    if (current.Length != 0 && target.Length != 0)
                    {
                        PresetIO.SetActivePreset(def, target);
                        PresetIO.SaveCurrentToPreset(menu, def, target);

                        if (!string.Equals(current, target, StringComparison.Ordinal))
                        {
                            if (current != Plugin.DEFAULTPRESET)
                                PresetIO.DeletePreset(def, current);

                            _selected = _name = target;
                            Dirty = true;
                        }

                        _confirmDelete = false;
                        Plugin.Log.LogInfo($"[Presets] Saved {def.unitName}:{target}");
                    }
                }

                GUI.enabled = !string.IsNullOrWhiteSpace(_selected) && _selected != Plugin.DEFAULTPRESET;

                if (GUILayout.Button(_confirmDelete ? "Confirm" : "Delete"))
                {
                    if (!_confirmDelete) _confirmDelete = true;
                    else
                    {
                        PresetIO.DeletePreset(def, _selected);

                        Plugin.Log.LogInfo($"[Presets] Deleted {def.unitName} / {_selected}");

                        _selected = "";
                        _name = "";
                        _confirmDelete = false;
                        Dirty = true;
                    }
                }

                GUI.enabled = true;
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Done", GUILayout.Width(70f)))
                {
                    _edit = false;
                    _confirmDelete = false;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();

            if (Event.current.type == EventType.Repaint)
                _desiredH = Mathf.Max(80, GUILayoutUtility.GetLastRect().yMax + 22);
        }
    }
}