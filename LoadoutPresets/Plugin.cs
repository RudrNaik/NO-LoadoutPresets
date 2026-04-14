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
        internal static readonly AccessTools.FieldRef<LoadoutSelector, Slider> FuelLevel =
            AccessTools.FieldRefAccess<LoadoutSelector, Slider>("fuelLevel");

        internal static readonly AccessTools.FieldRef<LoadoutSelector, TMP_Dropdown> LiveryDropdown =
            AccessTools.FieldRefAccess<LoadoutSelector, TMP_Dropdown>("liveryDropdown");

        internal static readonly AccessTools.FieldRef<LoadoutSelector, List<ValueTuple<LiveryKey, string>>> LiveryOptions =
            AccessTools.FieldRefAccess<LoadoutSelector, List<ValueTuple<LiveryKey, string>>>("liveryOptions");

        internal static readonly AccessTools.FieldRef<LoadoutSelector, List<WeaponSelector>> WeaponSelectors =
            AccessTools.FieldRefAccess<LoadoutSelector, List<WeaponSelector>>("weaponSelectors");

        // This one in particular is still in the AircraftSelectionMenu
        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, Aircraft> PreviewAircraft =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, Aircraft>("previewAircraft");

        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, LoadoutSelector> LoadoutSelectorRef =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, LoadoutSelector>("loadoutSelector");
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

        internal static bool IsSaved(AircraftDefinition def, string preset)
        {
            if (preset == Plugin.DEFAULTPRESET)
                return true; // IMPORTANT: treat default as always valid

            return Get(PresetSection(def, preset), SavedKey, false);
        }

        internal static void SaveCurrentToPreset(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            if (string.IsNullOrWhiteSpace(preset)) return;

            var loadoutSelector = MenuRefs.LoadoutSelectorRef(menu);

            string section = PresetSection(def, preset);
            Set(section, SavedKey, false, true);

            Set(section, FuelKey, 1f, Mathf.Clamp01(MenuRefs.FuelLevel(loadoutSelector).value));

            string liveryStr = "";
            TMP_Dropdown dd = MenuRefs.LiveryDropdown(loadoutSelector);
            var opts = MenuRefs.LiveryOptions(loadoutSelector);

            if (dd != null && opts != null && (uint)dd.value < (uint)opts.Count)
                liveryStr = opts[dd.value].Item1.ToString();

            Set(section, LiveryKey, "", liveryStr);

            var weaponSelectors = MenuRefs.WeaponSelectors(loadoutSelector);

            for (int i = 0; i < weaponSelectors.Count; i++)
            {
                WeaponMount mount = weaponSelectors[i].GetValue();
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
            var loadSelect = MenuRefs.LoadoutSelectorRef(menu);

            preset = Norm(preset);
            if (string.IsNullOrWhiteSpace(preset))
                return false;

            Aircraft preview = MenuRefs.PreviewAircraft(menu);
            HardpointSet[] sets = preview?.weaponManager?.hardpointSets;

            if (sets == null)
                return false;

            string section = PresetSection(def, preset);
            bool hasSaved = Get(section, SavedKey, false);

            if (!hasSaved || preset == Plugin.DEFAULTPRESET)
            {
                ApplyVanillaDefaults(menu, loadSelect);
                return true;
            }

            var weaponSelectors = MenuRefs.WeaponSelectors(loadSelect);
            int n = Math.Min(weaponSelectors.Count, sets.Length);

            for (int i = 0; i < n; i++)
            {
                string key = Get(section, HpKey(i), "") ?? "";

                weaponSelectors[i].SetValue(
                    string.IsNullOrEmpty(key)
                        ? null
                        : sets[i].weaponOptions.Find(w => w != null && w.jsonKey == key)
                );
            }

            loadSelect.UpdateWeapons(true);

            string wantLiveryKey = Get(section, LiveryKey, "") ?? "";

            if (!string.IsNullOrEmpty(wantLiveryKey))
            {
                TMP_Dropdown dd = MenuRefs.LiveryDropdown(loadSelect);
                var opts = MenuRefs.LiveryOptions(loadSelect);

                if (dd != null && opts != null && opts.Count > 0)
                {
                    int idx = opts.FindIndex(o => o.Item1.ToString() == wantLiveryKey);

                    if (idx >= 0 && idx < opts.Count)
                    {
                        dd.SetValueWithoutNotify(idx);
                        loadSelect.SelectLivery();
                    }
                }
            }

            float fuel = Get(section, FuelKey, 1f);

            var fuelSlider = MenuRefs.FuelLevel(loadSelect);
            if (fuelSlider != null)
            {
                fuelSlider.SetValueWithoutNotify(Mathf.Clamp01(fuel));
                loadSelect.ChangeFuelLevel();
            }

            return true;
        }

        private static void ApplyVanillaDefaults(
    AircraftSelectionMenu menu,
    LoadoutSelector loadSelect)
{
    var aircraft = MenuRefs.PreviewAircraft(menu);
    if (aircraft == null || aircraft.definition == null)
        return;

    var parameters = aircraft.definition.aircraftParameters;
    var weaponSelectors = MenuRefs.WeaponSelectors(loadSelect);

    if (parameters?.loadouts != null && parameters.loadouts.Count > 0)
    {
        var defaultLoadout =
            parameters.loadouts.Count > 1
                ? parameters.loadouts[1]
                : parameters.loadouts[0];

        int n = Math.Min(weaponSelectors.Count, defaultLoadout.weapons.Count);

        for (int i = 0; i < n; i++)
        {
            weaponSelectors[i].SetValue(defaultLoadout.weapons[i]);
        }
    }

    loadSelect.UpdateWeapons(false);

    var dd = MenuRefs.LiveryDropdown(loadSelect);
    var opts = MenuRefs.LiveryOptions(loadSelect);

    if (dd != null && opts != null && opts.Count > 0)
    {
        int livery = UnityEngine.Random.Range(0, opts.Count);
        dd.SetValueWithoutNotify(livery);
        loadSelect.SelectLivery();

        aircraft.SetLiveryKey(aircraft.NetworkLiveryKey, true);
    }

    var fuelSlider = MenuRefs.FuelLevel(loadSelect);

    if (fuelSlider != null)
    {
        float fuel = parameters != null
            ? parameters.DefaultFuelLevel
            : 1f;

        fuelSlider.SetValueWithoutNotify(Mathf.Clamp01(fuel));
        loadSelect.ChangeFuelLevel();
    }

    loadSelect.UpdateWeapons(true);
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

    [HarmonyPatch(typeof(LoadoutSelector), "LoadDefaults")]
    internal static class Patch_LoadDefaults
    {
        static void Postfix(LoadoutSelector __instance)
        {
            if (!Plugin.Enabled.Value) return;

            var menu = __instance.GetComponentInParent<AircraftSelectionMenu>();
            if (menu == null) return;

            PresetMenuUI.Attach(menu);

            // IMPORTANT: delay preset apply ONE frame
            menu.StartCoroutine(ApplyAfterFrame(menu));
        }

        private static System.Collections.IEnumerator ApplyAfterFrame(AircraftSelectionMenu menu)
        {
            yield return null; // wait for Unity to finish LoadDefaults internals

            var def = menu.GetSelectedType();
            var active = PresetIO.GetActivePreset(def);

            PresetIO.LoadPreset(menu, def, active);
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