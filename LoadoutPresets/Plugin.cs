using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
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

            Enabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "Enable / disable the mod."
            );

            new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();
        }

        private void OnGUI()
        {
            if (!Enabled.Value)
                return;

            PresetMenuUI.Draw();
        }
    }

    internal static class MenuRefs
    {
        internal static readonly AccessTools.FieldRef<LoadoutSelector, Slider> FuelLevel =
            AccessTools.FieldRefAccess<LoadoutSelector, Slider>("fuelLevel");

        internal static readonly AccessTools.FieldRef<LoadoutSelector, TMP_Dropdown> LiveryDropdown =
            AccessTools.FieldRefAccess<LoadoutSelector, TMP_Dropdown>("liveryDropdown");

        internal static readonly AccessTools.FieldRef<LoadoutSelector, List<ValueTuple<LiveryKey, string>>> LiveryOptions =
            AccessTools.FieldRefAccess<LoadoutSelector, List<ValueTuple<LiveryKey, string>>>("liveryOptions");

        internal static readonly AccessTools.FieldRef<LoadoutSelector, List<WeaponSelector>> WeaponSelectors =
            AccessTools.FieldRefAccess<LoadoutSelector, List<WeaponSelector>>("weaponSelectors");

        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, Aircraft> PreviewAircraft =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, Aircraft>("previewAircraft");

        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, LoadoutSelector> LoadoutSelectorRef =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, LoadoutSelector>("loadoutSelector");
    }

    internal static class PresetIO
    {
        internal static bool IsApplyingPreset;
        internal static bool IsRestoringDefaults;

        private const string ActiveKey = "ActivePreset";
        private const string SavedKey = "Saved";
        private const string FuelKey = "Fuel";
        private const string LiveryKey = "Livery";

        private static string HpKey(int i) => $"Hardpoint_{i}";

        private static string Norm(string preset)
        {
            preset = Regex.Replace(
                (preset ?? "").Trim(),
                @"[=\r\n\t\\\""'\[\]]",
                "_"
            );

            return preset.Length == 0
                ? Plugin.DEFAULTPRESET
                : preset;
        }

        private static ConfigEntry<T> Entry<T>(
            string section,
            string key,
            T def
        ) => Plugin.Cfg.Bind(section, key, def);

        internal static T Get<T>(
            string section,
            string key,
            T def
        ) => Entry(section, key, def).Value;

        internal static void Set<T>(
            string section,
            string key,
            T def,
            T value
        ) => Entry(section, key, def).Value = value;

        internal static string BaseSection(AircraftDefinition def) =>
            $"Aircraft:{def.unitName}";

        internal static string PresetSection(
            AircraftDefinition def,
            string preset
        ) => $"{BaseSection(def)}:{Norm(preset)}";

        internal static string GetActivePreset(AircraftDefinition def) =>
            Norm(Get(BaseSection(def), ActiveKey, Plugin.DEFAULTPRESET));

        internal static void SetActivePreset(
            AircraftDefinition def,
            string preset
        ) => Set(
            BaseSection(def),
            ActiveKey,Plugin.DEFAULTPRESET, Norm(preset)
        );

        internal static bool IsSaved(AircraftDefinition def,string preset)
        {
            if (preset == Plugin.DEFAULTPRESET)
                return true;

            return Get(PresetSection(def, preset),SavedKey,false);
        }

        internal static bool IsPresetEmpty(AircraftDefinition def,string preset)
        {
            string section = PresetSection(def, preset);

            for (int i = 0; i < 20; i++)
            {
                string hp =Get(section, HpKey(i), "");

                if (!string.IsNullOrWhiteSpace(hp))
                    return false;
            }

            return true;
        }

        internal static void ForceVanillaDefaults(LoadoutSelector loadSelect)
        {
            var method = typeof(LoadoutSelector).GetMethod("LoadDefaults", BindingFlags.Instance |BindingFlags.Public |BindingFlags.NonPublic);

            if (method == null)
            {
                Plugin.Log.LogError("[Presets] Failed to find LoadDefaults");
                return;
            }

            Patch_LoadDefaults.AllowVanilla = true;

            try
            {
                method.Invoke(loadSelect, null);
            }
            finally
            {
                Patch_LoadDefaults.AllowVanilla = false;
            }
        }

        internal static void SaveCurrentToPreset(AircraftSelectionMenu menu,AircraftDefinition def,string preset)
        {
            if (string.IsNullOrWhiteSpace(preset))
                return;

            var loadSelect = MenuRefs.LoadoutSelectorRef(menu);

            if (loadSelect == null)
                return;

            string section = PresetSection(def, preset);

            Set(section, SavedKey, false, true);

            var fuelSlider = MenuRefs.FuelLevel(loadSelect);

            if (fuelSlider != null)
            {
                Set(
                    section,
                    FuelKey,
                    1f,
                    Mathf.Clamp01(fuelSlider.value)
                );
            }

            string liveryStr = "";

            TMP_Dropdown dd = MenuRefs.LiveryDropdown(loadSelect);
            var opts = MenuRefs.LiveryOptions(loadSelect);

            if (dd != null &&
                opts != null &&
                (uint)dd.value < (uint)opts.Count)
            {
                liveryStr = opts[dd.value].Item1.ToString();
            }

            Set(section, LiveryKey, "", liveryStr);

            var selectors = MenuRefs.WeaponSelectors(loadSelect);

            if (selectors != null)
            {
                for (int i = 0; i < selectors.Count; i++)
                {
                    WeaponMount mount = selectors[i].GetValue();

                    Set(section,HpKey(i),"",mount?.jsonKey ?? "");
                }
            }

            Plugin.Cfg.Save();
        }

        internal static bool LoadPreset(
            AircraftSelectionMenu menu,
            AircraftDefinition def,
            string preset
        )
        {
            preset = Norm(preset);

            bool applied = ApplyPreset(menu, def, preset);

            Plugin.Cfg.Save();

            Plugin.Log.LogInfo($"[Presets] Loaded {preset}:{def.unitName} " + $"{(applied ? "" : "(no saved preset yet)")}");

            return applied;
        }

        internal static bool ApplyPreset(
            AircraftSelectionMenu menu,
            AircraftDefinition def,
            string preset
        )
        {
            preset = Norm(preset);

            if (string.IsNullOrWhiteSpace(preset))
                return false;

            string section = PresetSection(def, preset);

            bool hasSaved = Get(section, SavedKey, false);

            if (!hasSaved)
                return false;

            var loadSelect = MenuRefs.LoadoutSelectorRef(menu);

            if (loadSelect == null)
                return false;

            Aircraft preview = MenuRefs.PreviewAircraft(menu);

            if (preview == null || preview.weaponManager == null)
                return false;

            var sets = preview.weaponManager.hardpointSets;

            if (sets == null)
                return false;

            IsApplyingPreset = true;

            try
            {
                var weaponSelectors = MenuRefs.WeaponSelectors(loadSelect);

                int n = Math.Min(weaponSelectors.Count,sets.Length);

                for (int i = 0; i < n; i++)
                {
                    string key =
                        Get(section, HpKey(i), "") ?? "";

                    WeaponMount resolved = null;

                    if (!string.IsNullOrEmpty(key))
                    {
                        resolved = sets[i]
                            .weaponOptions?
                            .Find(w =>
                                w != null &&
                                w.jsonKey == key);

                        if (resolved == null)
                        {
                            resolved = Resources
                                .FindObjectsOfTypeAll<WeaponMount>()
                                .FirstOrDefault(w =>
                                    w != null &&
                                    w.jsonKey == key);

                            if (resolved != null)
                            {
                                Plugin.Log.LogInfo(
                                    $"[Presets] Resolved unrestricted weapon '{key}' globally."
                                );
                            }
                        }

                        if (resolved != null &&
                            !sets[i].weaponOptions.Contains(resolved))
                        {
                            sets[i].weaponOptions.Add(resolved);

                            Plugin.Log.LogInfo(
                                $"[Presets] Injected unrestricted weapon '{resolved.jsonKey}' into hardpoint {i}."
                            );
                        }
                    }

                    weaponSelectors[i].SetValue(resolved);
                }

                loadSelect.UpdateWeapons(true);

                float fuel = Get(section, FuelKey, 1f);

                var fuelSlider =MenuRefs.FuelLevel(loadSelect);

                if (fuelSlider != null)
                {
                    fuelSlider.SetValueWithoutNotify(Mathf.Clamp01(fuel));

                    loadSelect.ChangeFuelLevel();
                }

                string wantLiveryKey =Get(section, LiveryKey, "") ?? "";

                if (!string.IsNullOrEmpty(wantLiveryKey))
                {
                    menu.StartCoroutine(ApplyLiveryNextFrame(loadSelect,wantLiveryKey));
                }

                menu.StartCoroutine(RebuildWeaponsNextFrame(menu,preview));
            }
            finally
            {
                IsApplyingPreset = false;
            }

            return true;
        }

        internal static System.Collections.IEnumerator RebuildWeaponsNextFrame(AircraftSelectionMenu menu, Aircraft aircraft)
        {
            yield return null;

            var wm = aircraft.weaponManager;

            if (wm == null)
                yield break;

            wm.RemoveWeapons();
            wm.SpawnWeapons();

            var method =typeof(AircraftSelectionMenu).GetMethod("AircraftSelectionMenu_OnChange", BindingFlags.NonPublic | BindingFlags.Instance);

            method?.Invoke(menu, null);
        }

        internal static System.Collections.IEnumerator ApplyLiveryNextFrame(LoadoutSelector loadSelect,string wantLiveryKey)
        {
            yield return null;

            TMP_Dropdown dd = MenuRefs.LiveryDropdown(loadSelect);

            var opts = MenuRefs.LiveryOptions(loadSelect);

            if (dd == null || opts == null || opts.Count == 0)
            {
                yield break;
            }

            int idx =
                opts.FindIndex(o => o.Item1.ToString() == wantLiveryKey);

            if (idx >= 0 && idx < opts.Count)
            {
                dd.SetValueWithoutNotify(idx);
                loadSelect.SelectLivery();
            }
        }

        internal static void DeletePreset(AircraftDefinition def, string preset )
        {
            preset = preset?.Trim();

            if (string.IsNullOrWhiteSpace(preset) || preset == Plugin.DEFAULTPRESET)
            {
                return;
            }

            string section = PresetSection(def, preset);

            foreach (var k in Plugin.Cfg.Keys.ToArray())
            {
                if (string.Equals(k.Section,section,StringComparison.Ordinal))
                {
                    Plugin.Cfg.Remove(k);
                }
            }

            if (string.Equals(GetActivePreset(def),preset,StringComparison.Ordinal))
            {
                SetActivePreset(def, Plugin.DEFAULTPRESET);
            }

            Plugin.Cfg.Save();
            Plugin.Cfg.Reload();
        }

        internal static List<string> ListPresets(AircraftDefinition def)
        {
            HashSet<string> names = new(StringComparer.Ordinal)
            {
                    Plugin.DEFAULTPRESET
            };

            try
            {
                string path = Plugin.Cfg.ConfigFilePath;

                if (File.Exists(path))
                {
                    string prefix = $"[{BaseSection(def)}:";

                    foreach (string line in File.ReadLines(path))
                    {
                        if (!line.StartsWith(prefix,StringComparison.Ordinal))
                            continue;

                        if (!line.EndsWith("]",StringComparison.Ordinal))
                            continue;

                        string name = line.Substring(prefix.Length, line.Length -prefix.Length -1).Trim();

                        if (name.Length != 0)
                            names.Add(name);
                    }
                }
            }
            catch
            {
            }

            List<string> list = new() { Plugin.DEFAULTPRESET };

            List<string> rest = new();

            foreach (string p in names)
            {
                if (p == Plugin.DEFAULTPRESET)
                    continue;

                if (IsSaved(def, p))
                    rest.Add(p);
            }

            rest.Sort(StringComparer.Ordinal);

            list.AddRange(rest);

            return list;
        }

        internal static string ResolveWeaponName(AircraftSelectionMenu menu,string jsonKey)
        {
            if (string.IsNullOrEmpty(jsonKey))
                return null;

            var aircraft =
                MenuRefs.PreviewAircraft(menu);

            if (aircraft?.weaponManager == null)
                return jsonKey;

            var sets = aircraft.weaponManager.hardpointSets;

            if (sets == null)
                return jsonKey;

            foreach (var set in sets)
            {
                if (set?.weaponOptions == null)
                    continue;

                var match =
                    set.weaponOptions.FirstOrDefault(w => w != null &&w.jsonKey == jsonKey);

                if (match != null)
                    return match.mountName;
            }

            return jsonKey;
        }

        internal static string BuildPresetTooltip(AircraftSelectionMenu menu, AircraftDefinition def, string preset
        )
        {
            preset = Norm(preset);

            string section = PresetSection(def, preset);

            if (!Get(section, SavedKey, false))
            {
                return preset == Plugin.DEFAULTPRESET
                    ? "Current live loadout (auto-saved)"
                    : "No saved data";
            }

            StringBuilder sb = new();

            sb.AppendLine($"Preset: {preset}");

            float fuel = Get(section, FuelKey, 1f);

            sb.AppendLine($"Fuel: {(int)(fuel * 100f)}%");

            string livery =
                Get(section, LiveryKey, "");

            if (!string.IsNullOrEmpty(livery))
            {
                sb.AppendLine($"Livery: {livery}");
            }

            var counts = new Dictionary<string, int>();

            for (int i = 0; i < 20; i++)
            {
                string key =
                    Get<string>(section,HpKey(i),"");

                if (string.IsNullOrEmpty(key))
                    continue;

                if (!counts.ContainsKey(key))
                    counts[key] = 0;

                counts[key]++;
            }

            if (counts.Count == 0)
            {
                sb.AppendLine("Weapons: None");
            }
            else
            {
                sb.AppendLine("Weapons:");

                foreach (var kv in counts.OrderByDescending(k => k.Value))
                {
                    string displayName = ResolveWeaponName(menu, kv.Key);
                    sb.AppendLine($"{displayName} x{kv.Value}");
                }
            }

            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(LoadoutSelector), "LoadDefaults")]
    internal static class Patch_LoadDefaults
    {
        internal static bool AllowVanilla;

        static bool Prefix()
        {
            if (PresetIO.IsRestoringDefaults)
                return true;

            return AllowVanilla;
        }

        static void Postfix(LoadoutSelector __instance)
        {
            if (!Plugin.Enabled.Value)
                return;

            var menu =
                __instance.GetComponentInParent<AircraftSelectionMenu>();

            if (menu == null)
                return;

            PresetMenuUI.Attach(menu);

            var def = menu.GetSelectedType();

            if (def == null)
                return;

            bool empty = PresetIO.IsPresetEmpty(def,Plugin.DEFAULTPRESET);

            if (empty)
            {
                if (PresetIO.IsRestoringDefaults)
                {
                    Plugin.Log.LogWarning($"[Presets] Prevented recursive default restore for {def.unitName}");

                    return;
                }

                try
                {
                    PresetIO.IsRestoringDefaults = true;
                    Plugin.Log.LogInfo($"[Presets] DEFAULT empty for {def.unitName}, restoring vanilla defaults");
                    PresetIO.ForceVanillaDefaults(__instance);
                    PresetIO.SaveCurrentToPreset(menu,def,Plugin.DEFAULTPRESET);
                }
                finally
                {
                    PresetIO.IsRestoringDefaults = false;
                }

                return;
            }


            PresetIO.LoadPreset(menu,def,Plugin.DEFAULTPRESET);
        }
    }

    [HarmonyPatch(typeof(LoadoutSelector), "UpdateWeapons")]
    internal static class Patch_AutoSave_Default
    {
        static void Postfix(LoadoutSelector __instance)
        {
            AutoSave(__instance);
        }

        internal static void AutoSave(LoadoutSelector __instance)
        {
            if (!Plugin.Enabled.Value)
                return;

            if (PresetIO.IsApplyingPreset)
                return;

            var menu = __instance.GetComponentInParent<AircraftSelectionMenu>();

            if (menu == null)
                return;

            var def = menu.GetSelectedType();

            if (def == null)
                return;

            var selectors = MenuRefs.WeaponSelectors(__instance);

            if (selectors == null || selectors.Count == 0)
            {
                return;
            }

            if (!selectors.Any(s => s.GetValue() != null))
                return;

            PresetIO.SaveCurrentToPreset(menu, def, Plugin.DEFAULTPRESET);

            Plugin.Log.LogInfo("Auto-saved last used.");
        }
    }

    [HarmonyPatch(typeof(LoadoutSelector), "ChangeFuelLevel")]
    internal static class Patch_AutoSave_Fuel
    {
        static void Postfix(LoadoutSelector __instance)
        {
            Patch_AutoSave_Default.AutoSave(__instance);
        }
    }

    [HarmonyPatch(typeof(LoadoutSelector), "SelectLivery")]
    internal static class Patch_AutoSave_Livery
    {
        static void Postfix(LoadoutSelector __instance)
        {
            Patch_AutoSave_Default.AutoSave(__instance);
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
        private static float right_Padding = 5f;
        private static float _desiredH;
        private static string _cachedUnitName = "";
        private static List<string> _presets = [];

        private static string _currentTooltip = "";

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
            _rect.x = Screen.width - _rect.width - right_Padding;

            _rect = GUI.Window(2082, _rect, _ => Window(menu, def), "Loadout Presets");
            DrawTooltip();
        }

        private static void DrawTooltip()
        {
            if (string.IsNullOrEmpty(_currentTooltip))
                return;

            GUI.depth = -1000; // force on top

            Vector2 mouse = Event.current.mousePosition;
            mouse = GUIUtility.GUIToScreenPoint(mouse);

            Vector2 size = GUI.skin.box.CalcSize(new GUIContent(_currentTooltip));

            Rect rect = new Rect(
                mouse.x - size.x - 15f,
                mouse.y + 15f,
                size.x + 10f,
                size.y + 6f
            );

            GUI.color = new Color(1f, 1f, 1f, 2f);

            GUI.Box(rect, _currentTooltip);
        }

        private static void Window(AircraftSelectionMenu menu, AircraftDefinition def)
        {
            _currentTooltip = GUI.tooltip;

            string active = PresetIO.GetActivePreset(def);
            string focus = string.IsNullOrWhiteSpace(_selected) ? active : _selected;

            GUILayout.BeginVertical();

            for (int i = 0; i < _presets.Count; i++)
            {
                string p = _presets[i];
                bool isFocus = string.Equals(p, focus, StringComparison.Ordinal);
                GUIStyle style = isFocus ? GUI.skin.label : GUI.skin.button;

                GUIContent loadoutContent = new GUIContent(p, PresetIO.BuildPresetTooltip(menu, def, p));

                if (GUILayout.Button(loadoutContent, style))
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
                GUIContent editContent = new GUIContent("Edit", "Edit, create, or delete loadout presets");

                if (GUILayout.Button(editContent, GUILayout.Width(70f)))
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
            {
                _desiredH = Mathf.Max(80, GUILayoutUtility.GetLastRect().yMax + 22);
                _currentTooltip = GUI.tooltip;
            }
        }
    }
}