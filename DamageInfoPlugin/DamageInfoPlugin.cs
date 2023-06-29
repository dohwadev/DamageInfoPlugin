using Dalamud.Game.Command;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud;
using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Logging;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using static DamageInfoPlugin.LogType;
using Action = Lumina.Excel.GeneratedSheets.Action;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace DamageInfoPlugin
{
    public unsafe class DamageInfoPlugin : IDalamudPlugin
    {
        private const int TargetInfoGaugeBgNodeIndex = 41;
        private const int TargetInfoGaugeNodeIndex = 43;

        private const int TargetInfoSplitGaugeBgNodeIndex = 2;
        private const int TargetInfoSplitGaugeNodeIndex = 4;

        private const int FocusTargetInfoGaugeBgNodeIndex = 13;
        private const int FocusTargetInfoGaugeNodeIndex = 15;

        public string Name => "Damage Info";

        private const string CommandName = "/dmginfo";

        private readonly Configuration _configuration;
        private readonly PluginUI _ui;

        private readonly GameGui _gameGui;
        private readonly CommandManager _cmdMgr;
        private readonly FlyTextGui _ftGui;
        private readonly ObjectTable _objectTable;
        private readonly ClientState _clientState;
        private readonly TargetManager _targetManager;

        private delegate void AddScreenLogDelegate(
            Character* target,
            Character* source,
            FlyTextKind logKind,
            int option,
            int actionKind,
            int actionId,
            int val1,
            int val2,
            int val3,
            int val4);

        private delegate void ReceiveActionEffectDelegate(uint sourceId, Character* sourceCharacter, IntPtr pos, EffectHeader* effectHeader, EffectEntry* effectArray, ulong* effectTail);
        private delegate void SetCastBarDelegate(IntPtr thisPtr, IntPtr a2, IntPtr a3, IntPtr a4, char a5);

        private readonly Hook<AddScreenLogDelegate> _addScreenLogHook;
        private readonly Hook<ReceiveActionEffectDelegate> _receiveActionEffectHook;
        private readonly Hook<SetCastBarDelegate> _setCastBarHook;
        private readonly Hook<SetCastBarDelegate> _setFocusTargetCastBarHook;

        private readonly CastbarInfo _nullCastbarInfo;
        private Dictionary<uint, DamageType> _actionToDamageTypeDict;
        private readonly HashSet<uint> _ignoredCastActions;
        private ActionEffectStore _actionStore;
        
        // These are the skills' percentage potency increases sent by the server
        // check research.csv for more info
        private readonly Dictionary<int, HashSet<int>> _positionalData = new()
        {
            {   56, new HashSet<int> {19, 21}},                 // Snap Punch
            {   66, new HashSet<int> {46, 60}},                 // Demolish
            // {   79, new HashSet<int> {}},                // Heavy Thrust
            {   88, new HashSet<int> {28, 61}},             // Chaos Thrust
            { 2255, new HashSet<int> {30, 37, 68, 75}},             // Aeolian Edge
            { 2258, new HashSet<int> {25}},                 // Trick Attack
            { 3554, new HashSet<int> {10, 13}},             // Fang and Claw
            { 3556, new HashSet<int> {10, 13}},             // Wheeling Thrust
            { 3563, new HashSet<int> {30, 37, 66, 73}},             // Armor Crush
            { 7481, new HashSet<int> {29, 33, 68, 72}},     // Gekko (rear)
            { 7482, new HashSet<int> {29, 33, 68, 72}},     // Kasha (flank)
            {24382, new HashSet<int> {11, 13}},             // Gibbet (flank)
            {24383, new HashSet<int> {11, 13}},             // Gallows (rear)
            {25772, new HashSet<int> {28, 66}},             // Chaotic Spring
        };
        
        public DamageInfoPlugin(
            [RequiredVersion("1.0")] GameGui gameGui,
            [RequiredVersion("1.0")] FlyTextGui ftGui,
            [RequiredVersion("1.0")] DalamudPluginInterface pi,
            [RequiredVersion("1.0")] CommandManager cmdMgr,
            [RequiredVersion("1.0")] DataManager dataMgr,
            [RequiredVersion("1.0")] ObjectTable objectTable,
            [RequiredVersion("1.0")] ClientState clientState,
            [RequiredVersion("1.0")] TargetManager targetManager,
            [RequiredVersion("1.0")] SigScanner scanner)
        {
            _gameGui = gameGui;
            _ftGui = ftGui;
            _cmdMgr = cmdMgr;
            _objectTable = objectTable;
            _clientState = clientState;
            _targetManager = targetManager;

            _configuration = LoadConfig(pi);
            _ui = new PluginUI(_configuration, this);
            _actionToDamageTypeDict = new Dictionary<uint, DamageType>();
            _ignoredCastActions = new HashSet<uint>();
            _actionStore = new ActionEffectStore(_configuration);
            _nullCastbarInfo = new CastbarInfo { unitBase = null, gauge = null, bg = null };

            cmdMgr.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Display the Damage Info configuration interface.",
            });

            try
            {
                var actionSheet = dataMgr.GetExcelSheet<Action>();

                if (actionSheet == null)
                    throw new NullReferenceException();
                
                foreach (var row in actionSheet)
                {
                    var tmpType = (DamageType)row.AttackType.Row;
                    if (tmpType != DamageType.Magic
                        && tmpType != DamageType.Darkness
                        && tmpType != DamageType.Unknown)
                        tmpType = DamageType.Physical;

                    _actionToDamageTypeDict.Add(row.RowId, tmpType);

                    if (row.ActionCategory.Row is > 4 and < 11)
                        _ignoredCastActions.Add(row.ActionCategory.Row);
                }

                var receiveActionEffectFuncPtr = scanner.ScanText("4C 89 44 24 ?? 55 56 41 54 41 55 41 56");
                _receiveActionEffectHook = Hook<ReceiveActionEffectDelegate>.FromAddress(receiveActionEffectFuncPtr, ReceiveActionEffect);

                var addScreenLogPtr = scanner.ScanText("E8 ?? ?? ?? ?? BF ?? ?? ?? ?? EB 3A");
                _addScreenLogHook = Hook<AddScreenLogDelegate>.FromAddress(addScreenLogPtr, AddScreenLogDetour);

                var setCastBarFuncPtr = scanner.ScanText("E8 ?? ?? ?? ?? 4C 8D 8F ?? ?? ?? ?? 4D 8B C6");
                _setCastBarHook = Hook<SetCastBarDelegate>.FromAddress(setCastBarFuncPtr, SetCastBarDetour);

                var setFocusTargetCastBarFuncPtr = scanner.ScanText("E8 ?? ?? ?? ?? 49 8B 47 20 4C 8B 6C 24");
                _setFocusTargetCastBarHook = Hook<SetCastBarDelegate>.FromAddress(setFocusTargetCastBarFuncPtr, SetFocusTargetCastBarDetour);

                ftGui.FlyTextCreated += OnFlyTextCreated;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"An error occurred loading DamageInfoPlugin.");
                PluginLog.Error("Plugin will not be loaded.");

                _addScreenLogHook?.Disable();
                _addScreenLogHook?.Dispose();
                _receiveActionEffectHook?.Disable();
                _receiveActionEffectHook?.Dispose();
                _setCastBarHook?.Disable();
                _setCastBarHook?.Dispose();
                _setFocusTargetCastBarHook?.Disable();
                _setFocusTargetCastBarHook?.Dispose();
                cmdMgr.RemoveHandler(CommandName);

                throw;
            }

            _receiveActionEffectHook.Enable();
            _addScreenLogHook.Enable();
            _setCastBarHook.Enable();
            _setFocusTargetCastBarHook.Enable();

            pi.UiBuilder.Draw += DrawUI;
            pi.UiBuilder.OpenConfigUi += DrawConfigUI;
        }

        private Configuration LoadConfig(DalamudPluginInterface pi)
        {
            var config = pi.GetPluginConfig() as Configuration ?? new Configuration();
            if (config.Version == 0)
            {
                config = new Configuration
                {
                    PhysicalColor = config.PhysicalColor,
                    MagicColor = config.MagicColor,
                    DarknessColor = config.DarknessColor,
                    PhysicalBgColor = config.PhysicalBgColor,
                    MagicBgColor = config.MagicBgColor,
                    DarknessBgColor = config.DarknessBgColor,
                    PhysicalCastColor = config.PhysicalCastColor,
                    MagicCastColor = config.MagicCastColor,
                    DarknessCastColor = config.DarknessCastColor,
                    MainTargetCastBarColorEnabled = config.MainTargetCastBarColorEnabled,
                    FocusTargetCastBarColorEnabled = config.FocusTargetCastBarColorEnabled
                };
            } 
            else if (config.Version == 1)
            {
                config.Version = 2;
                config.PositionalColorInvert = false;
            }
            
            config.Initialize(pi, this);
            config.Save();
            return config;
        }

        public void Dispose()
        {
            _actionStore.Dispose();
            ResetMainTargetCastBar();
            ResetFocusTargetCastBar();
            _receiveActionEffectHook?.Disable();
            _receiveActionEffectHook?.Dispose();
            _addScreenLogHook?.Disable();
            _addScreenLogHook?.Dispose();
            _setCastBarHook?.Disable();
            _setCastBarHook?.Dispose();
            _setFocusTargetCastBarHook?.Disable();
            _setFocusTargetCastBarHook?.Dispose();

            _ftGui.FlyTextCreated -= OnFlyTextCreated;

            _actionStore = null;
            _actionToDamageTypeDict = null;

            _ui.Dispose();
            _cmdMgr.RemoveHandler(CommandName);
        }

        private void OnCommand(string command, string args)
        {
            _ui.SettingsVisible = true;
        }

        private void DrawUI()
        {
            _ui.Draw();
        }

        private void DrawConfigUI()
        {
            _ui.SettingsVisible = true;
        }

        #region castbar
        private CastbarInfo GetTargetInfoUiElements()
        {
            var unitBase = (AtkUnitBase*)_gameGui.GetAddonByName("_TargetInfo", 1).ToPointer();

            if (unitBase == null) return _nullCastbarInfo;

            return new CastbarInfo
            {
                unitBase = unitBase,
                gauge = (AtkImageNode*)unitBase->UldManager.NodeList[TargetInfoGaugeNodeIndex],
                bg = (AtkImageNode*)unitBase->UldManager.NodeList[TargetInfoGaugeBgNodeIndex]
            };
        }

        private CastbarInfo GetTargetInfoSplitUiElements()
        {
            var unitBase = (AtkUnitBase*)_gameGui.GetAddonByName("_TargetInfoCastBar", 1).ToPointer();

            if (unitBase == null) return _nullCastbarInfo;

            return new CastbarInfo
            {
                unitBase = unitBase,
                gauge = (AtkImageNode*)unitBase->UldManager.NodeList[TargetInfoSplitGaugeNodeIndex],
                bg = (AtkImageNode*)unitBase->UldManager.NodeList[TargetInfoSplitGaugeBgNodeIndex]
            };
        }

        private CastbarInfo GetFocusTargetUiElements()
        {
            var unitBase = (AtkUnitBase*)_gameGui.GetAddonByName("_FocusTargetInfo", 1).ToPointer();

            if (unitBase == null) return _nullCastbarInfo;

            return new CastbarInfo
            {
                unitBase = unitBase,
                gauge = (AtkImageNode*)unitBase->UldManager.NodeList[FocusTargetInfoGaugeNodeIndex],
                bg = (AtkImageNode*)unitBase->UldManager.NodeList[FocusTargetInfoGaugeBgNodeIndex]
            };
        }

        public void ResetMainTargetCastBar()
        {
            var targetInfo = GetTargetInfoUiElements();
            var splitInfo = GetTargetInfoSplitUiElements();

            if (targetInfo.unitBase != null && targetInfo.gauge != null && targetInfo.bg != null)
            {
                targetInfo.gauge->AtkResNode.Color.R = 0xFF;
                targetInfo.gauge->AtkResNode.Color.G = 0xFF;
                targetInfo.gauge->AtkResNode.Color.B = 0xFF;
                targetInfo.gauge->AtkResNode.Color.A = 0xFF;

                targetInfo.bg->AtkResNode.Color.R = 0xFF;
                targetInfo.bg->AtkResNode.Color.G = 0xFF;
                targetInfo.bg->AtkResNode.Color.B = 0xFF;
                targetInfo.bg->AtkResNode.Color.A = 0xFF;
            }

            if (splitInfo.unitBase == null || splitInfo.gauge == null || splitInfo.bg == null) return;
            
            splitInfo.gauge->AtkResNode.Color.R = 0xFF;
            splitInfo.gauge->AtkResNode.Color.G = 0xFF;
            splitInfo.gauge->AtkResNode.Color.B = 0xFF;
            splitInfo.gauge->AtkResNode.Color.A = 0xFF;

            splitInfo.bg->AtkResNode.Color.R = 0xFF;
            splitInfo.bg->AtkResNode.Color.G = 0xFF;
            splitInfo.bg->AtkResNode.Color.B = 0xFF;
            splitInfo.bg->AtkResNode.Color.A = 0xFF;
        }

        public void ResetFocusTargetCastBar()
        {
            var ftInfo = GetFocusTargetUiElements();

            if (ftInfo.unitBase == null || ftInfo.gauge == null || ftInfo.bg == null) return;
            
            ftInfo.gauge->AtkResNode.Color.R = 0xFF;
            ftInfo.gauge->AtkResNode.Color.G = 0xFF;
            ftInfo.gauge->AtkResNode.Color.B = 0xFF;
            ftInfo.gauge->AtkResNode.Color.A = 0xFF;

            ftInfo.bg->AtkResNode.Color.R = 0xFF;
            ftInfo.bg->AtkResNode.Color.G = 0xFF;
            ftInfo.bg->AtkResNode.Color.B = 0xFF;
            ftInfo.bg->AtkResNode.Color.A = 0xFF;
        }

        private void SetCastBarDetour(IntPtr thisPtr, IntPtr a2, IntPtr a3, IntPtr a4, char a5)
        {
            if (!_configuration.MainTargetCastBarColorEnabled)
            {
                _setCastBarHook.Original(thisPtr, a2, a3, a4, a5);
                return;
            }

            var targetInfo = GetTargetInfoUiElements();
            var splitInfo = GetTargetInfoSplitUiElements();

            var combinedInvalid = targetInfo.unitBase == null || targetInfo.gauge == null || targetInfo.bg == null;
            var splitInvalid = splitInfo.unitBase == null || splitInfo.gauge == null || splitInfo.bg == null;

            if (combinedInvalid && splitInvalid)
            {
                _setCastBarHook.Original(thisPtr, a2, a3, a4, a5);
                return;
            }

            if (thisPtr.ToPointer() == targetInfo.unitBase && !combinedInvalid)
            {
                var mainTarget = _targetManager.Target;
                ColorCastBar(mainTarget, targetInfo, _setCastBarHook, thisPtr, a2, a3, a4, a5);
            }
            else if (thisPtr.ToPointer() == splitInfo.unitBase && !splitInvalid)
            {
                var mainTarget = _targetManager.Target;
                ColorCastBar(mainTarget, splitInfo, _setCastBarHook, thisPtr, a2, a3, a4, a5);
            }
        }

        private void SetFocusTargetCastBarDetour(IntPtr thisPtr, IntPtr a2, IntPtr a3, IntPtr a4, char a5)
        {
            if (!_configuration.FocusTargetCastBarColorEnabled)
            {
                _setFocusTargetCastBarHook.Original(thisPtr, a2, a3, a4, a5);
                return;
            }

            var ftInfo = GetFocusTargetUiElements();

            var focusTargetInvalid = ftInfo.unitBase == null || ftInfo.gauge == null || ftInfo.bg == null;

            if (thisPtr.ToPointer() != ftInfo.unitBase || focusTargetInvalid) return;
            
            var focusTarget = _targetManager.FocusTarget;
            ColorCastBar(focusTarget, ftInfo, _setFocusTargetCastBarHook, thisPtr, a2, a3, a4, a5);
        }

        private void ColorCastBar(GameObject target, CastbarInfo info, Hook<SetCastBarDelegate> hook,
            IntPtr thisPtr, IntPtr a2, IntPtr a3, IntPtr a4, char a5)
        {
            // DebugLog(Castbar, $"coloring cast bar");
            if (target == null || target is not BattleNpc battleTarget)
            {
                // DebugLog(Castbar, $"target {target}");
                // DebugLog(Castbar, $"type {target?.GetType()}");
                hook.Original(thisPtr, a2, a3, a4, a5);
                return;
            }

            var actionId = battleTarget.CastActionId;
            _actionToDamageTypeDict.TryGetValue(actionId, out var type);
            // DebugLog(Castbar, $"casting {actionId} {type}");
            if (_ignoredCastActions.Contains(actionId))
            {
                info.gauge->AtkResNode.Color.R = 0xFF;
                info.gauge->AtkResNode.Color.G = 0xFF;
                info.gauge->AtkResNode.Color.B = 0xFF;
                info.gauge->AtkResNode.Color.A = 0xFF;

                info.bg->AtkResNode.Color.R = 0xFF;
                info.bg->AtkResNode.Color.G = 0xFF;
                info.bg->AtkResNode.Color.B = 0xFF;
                info.bg->AtkResNode.Color.A = 0xFF;

                hook.Original(thisPtr, a2, a3, a4, a5);
                return;
            }

            var castColor = type switch
            {
                DamageType.Physical => _configuration.PhysicalCastColor,
                DamageType.Magic => _configuration.MagicCastColor,
                DamageType.Darkness => _configuration.DarknessCastColor,
                _ => Vector4.One
            };

            var bgColor = type switch
            {
                DamageType.Physical => _configuration.PhysicalBgColor,
                DamageType.Magic => _configuration.MagicBgColor,
                DamageType.Darkness => _configuration.DarknessBgColor,
                _ => Vector4.One
            };

            info.gauge->AtkResNode.Color.R = (byte)(castColor.X * 255);
            info.gauge->AtkResNode.Color.G = (byte)(castColor.Y * 255);
            info.gauge->AtkResNode.Color.B = (byte)(castColor.Z * 255);
            info.gauge->AtkResNode.Color.A = (byte)(castColor.W * 255);

            info.bg->AtkResNode.Color.R = (byte)(bgColor.X * 255);
            info.bg->AtkResNode.Color.G = (byte)(bgColor.Y * 255);
            info.bg->AtkResNode.Color.B = (byte)(bgColor.Z * 255);
            info.bg->AtkResNode.Color.A = (byte)(bgColor.W * 255);

            hook.Original(thisPtr, a2, a3, a4, a5);
        }

        #endregion

        private List<uint> FindCharaPets()
        {
            var results = new List<uint>();
            var charaId = GetCharacterActorId();
            foreach (var obj in _objectTable)
            {
                if (obj is not BattleNpc npc) continue;

                var actPtr = npc.Address;
                if (actPtr == IntPtr.Zero) continue;

                if (npc.OwnerId == charaId)
                    results.Add(npc.ObjectId);
            }

            return results;
        }

        private uint GetCharacterActorId()
        {
            return _clientState.LocalPlayer?.ObjectId ?? 0;
        }

        private SeString GetActorName(uint id)
        {
            return _objectTable.SearchById(id)?.Name ?? SeString.Empty;
        }
        
        private void ReceiveActionEffect(uint sourceId, Character* sourceCharacter, IntPtr pos, EffectHeader* effectHeader, EffectEntry* effectArray, ulong* effectTail)
        {
            try
            {
                _actionStore.Cleanup();
                
                DebugLog(Effect, $"--- source actor: {sourceCharacter->GameObject.ObjectID}, action id {effectHeader->ActionId}, anim id {effectHeader->AnimationId} numTargets: {effectHeader->TargetCount} ---");

                // TODO: Reimplement opcode logging, if it's even useful. Original code follows
                // ushort op = *((ushort*) effectHeader.ToPointer() - 0x7);
                // DebugLog(Effect, $"--- source actor: {sourceId}, action id {id}, anim id {animId}, opcode: {op:X} numTargets: {targetCount} ---");

                var entryCount = effectHeader->TargetCount switch
                {
                    0 => 0,
                    1 => 8,
                    <= 8 => 64,
                    <= 16 => 128,
                    <= 24 => 192,
                    <= 32 => 256,
                    _ => 0
                };

                // Check if we have data for this action ID.
                // Then we can check if the p2 is in the expected value set for positional success.
                var positionalState = PositionalState.Ignore;
                if (_positionalData.TryGetValue(effectHeader->AnimationId, out var actionPosData))
                {
                    positionalState = PositionalState.Failure;
                    for (int i = 0; i < entryCount; i++)
                        if (effectArray[i].type == ActionEffectType.Damage)
                            if (actionPosData.Contains(effectArray[i].param2))
                                positionalState = PositionalState.Success;
                }
                
                for (int i = 0; i < entryCount; i++)
                {
                    if (effectArray[i].type == ActionEffectType.Nothing) continue;

                    var target = effectTail[i / 8];
                    uint dmg = effectArray[i].value;
                    if (effectArray[i].mult != 0)
                        dmg += ((uint)ushort.MaxValue + 1) * effectArray[i].mult;

                    DebugLog(Effect, $"{effectArray[i]}, s: {sourceId} t: {target}");

                    var newEffect = new ActionEffectInfo
                    {
                        step = ActionStep.Effect,
                        actionId = effectHeader->ActionId,
                        type = effectArray[i].type,
                        // we fill in LogKind later 
                        sourceId = sourceId,
                        targetId = target,
                        value = dmg,
                        positionalState = positionalState,
                    };

                    _actionStore.AddEffect(newEffect);
                }
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "An error has occurred in Damage Info.");
            }

            _receiveActionEffectHook.Original(sourceId, sourceCharacter, pos, effectHeader, effectArray, effectTail);
        }

        private void AddScreenLogDetour(
            Character* target,
            Character* source,
            FlyTextKind logKind,
            int option,
            int actionKind,
            int actionId,
            int val1,
            int val2,
            int val3,
            int val4)
        {
            try
            {
                var targetId = target->GameObject.ObjectID;
                var sourceId = source->GameObject.ObjectID;

                if (_configuration.DebugLogEnabled)
                {
                    DebugLog(ScreenLog, $"{option} {actionKind} {actionId}");
                    DebugLog(ScreenLog, $"{val1} {val2} {val3} {val4}");
                    var targetName = GetActorName(targetId);
                    var sourceName = GetActorName(sourceId);
                    DebugLog(ScreenLog, $"src {sourceId} {sourceName}");
                    DebugLog(ScreenLog, $"tgt {targetId} {targetName}");
                }

                _actionStore.UpdateEffect((uint)actionId, sourceId, targetId, (uint)val1, logKind);
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "An error occurred in Damage Info.");
            }

            _addScreenLogHook.Original(target, source, logKind, option, actionKind, actionId, val1, val2, val3, val4);
        }

        private void OnFlyTextCreated(
            ref FlyTextKind kind,
            ref int val1,
            ref int val2,
            ref SeString text1,
            ref SeString text2,
            ref uint color,
            ref uint icon,
            ref float yOffset,
            ref bool handled)
        {
            try
            {
                var ftKind = kind;

                if (_configuration.DebugLogEnabled)
                {
                    var str1 = text1?.TextValue.Replace("%", "%%");
                    var str2 = text2?.TextValue.Replace("%", "%%");

                    DebugLog(FlyText, $"flytext created: kind: {ftKind} ({(int)kind}), val1: {val1}, val2: {val2}, color: {color:X}, icon: {icon}");
                    DebugLog(FlyText, $"text1: {str1} | text2: {str2}");
                }

                var charaId = GetCharacterActorId();
                var petIds = FindCharaPets();

                if (!_actionStore.TryGetEffect((uint)val1, ftKind, charaId, petIds, out var info))
                {
                    DebugLog(FlyText, $"Failed to obtain info... {val1} {ftKind}");
                    return;
                }

                DebugLog(FlyText, $"Obtained info: {info}");

                var isHealingAction = info.type == ActionEffectType.Heal;
                var isPetAction = petIds.Contains(info.sourceId);
                var isCharaAction = info.sourceId == charaId;
                var isCharaTarget = info.targetId == charaId;

                if ((_configuration.IncomingColorEnabled || _configuration.OutgoingColorEnabled || _configuration.PositionalColorEnabled)
                    && _actionToDamageTypeDict.TryGetValue(info.actionId, out var dmgType))
                {
                    var incomingCheck = !isCharaAction && isCharaTarget && !isHealingAction && _configuration.IncomingColorEnabled;
                    var outgoingCheck = isCharaAction && !isCharaTarget && !isHealingAction && _configuration.OutgoingColorEnabled;
                    var petCheck = !isCharaAction && !isCharaTarget && petIds.Contains(info.sourceId) && !isHealingAction && _configuration.PetColorEnabled;
                    
                    // Large check - check that it's a character action, we shouldn't ignore the state, and that positionals are enabled
                    // then, check to see if we should color the success or the failure
                    var posCheck = isCharaAction && info.positionalState != PositionalState.Ignore && _configuration.PositionalColorEnabled
                                   && ((info.positionalState == PositionalState.Success && !_configuration.PositionalColorInvert) ||
                                       (info.positionalState == PositionalState.Failure && _configuration.PositionalColorInvert));

                    if (incomingCheck || outgoingCheck || petCheck)
                        color = GetDamageColor(dmgType);

                    if (posCheck)
                        color = ImGui.GetColorU32(_configuration.PositionalColor);
                }

                if (_configuration.SourceTextEnabled || _configuration.PetSourceTextEnabled || _configuration.HealSourceTextEnabled)
                {
                    var tgtCheck = !isCharaAction && !isHealingAction && !isPetAction && _configuration.SourceTextEnabled;
                    var petCheck = isPetAction && _configuration.PetSourceTextEnabled;
                    var healCheck = isHealingAction && _configuration.HealSourceTextEnabled;
                
                    if (tgtCheck || petCheck || healCheck)
                    {
                        text2 = GetNewText(info.sourceId, text2);
                    }
                }
                
                // Attack text checks
                if (!_configuration.IncomingAttackTextEnabled
                    || !_configuration.OutgoingAttackTextEnabled
                    || !_configuration.PetAttackTextEnabled
                    || !_configuration.HealAttackTextEnabled)
                {
                    var incomingCheck = !isCharaAction && isCharaTarget && !isHealingAction && !isPetAction && !_configuration.IncomingAttackTextEnabled;
                    var outgoingCheck = isCharaAction && !isCharaTarget && !isHealingAction && !isPetAction && !_configuration.OutgoingAttackTextEnabled;
                    var petCheck = !isCharaAction && !isCharaTarget && !isHealingAction && isPetAction && !_configuration.PetAttackTextEnabled;
                    var healCheck = isHealingAction && !isPetAction && !_configuration.HealAttackTextEnabled;

                    if (incomingCheck || outgoingCheck || petCheck || healCheck)
                        text1 = "";
                }
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "An error has occurred in Damage Info");
            }
        }

        private SeString GetNewText(uint sourceId, SeString originalText)
        {
            SeString name = GetActorName(sourceId);
            var newPayloads = new List<Payload>();

            if (name.Payloads.Count == 0) return originalText;

            switch (_clientState.ClientLanguage)
            {
                case ClientLanguage.Japanese:
                    newPayloads.AddRange(name.Payloads);
                    newPayloads.Add(new TextPayload("から"));
                    break;
                case ClientLanguage.English:
                    newPayloads.Add(new TextPayload("from "));
                    newPayloads.AddRange(name.Payloads);
                    break;
                case ClientLanguage.German:
                    newPayloads.Add(new TextPayload("von "));
                    newPayloads.AddRange(name.Payloads);
                    break;
                case ClientLanguage.French:
                    newPayloads.Add(new TextPayload("de "));
                    newPayloads.AddRange(name.Payloads);
                    break;
                default:
                    newPayloads.Add(new TextPayload(">"));
                    newPayloads.AddRange(name.Payloads);
                    break;
            }

            if (originalText.Payloads.Count > 0)
                newPayloads.AddRange(originalText.Payloads);

            return new SeString(newPayloads);
        }

        private void DebugLog(LogType type, string str)
        {
            if (_configuration.DebugLogEnabled)
                PluginLog.Information($"[{type}] {str}");
        }

        private uint GetDamageColor(DamageType type, uint fallback = 0xFF00008A)
        {
            return type switch
            {
                DamageType.Physical => ImGui.GetColorU32(_configuration.PhysicalColor),
                DamageType.Magic => ImGui.GetColorU32(_configuration.MagicColor),
                DamageType.Darkness => ImGui.GetColorU32(_configuration.DarknessColor),
                _ => fallback
            };
        }
    }
}