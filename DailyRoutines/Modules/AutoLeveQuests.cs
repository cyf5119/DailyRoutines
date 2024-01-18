using System.Collections.Generic;
using System.Linq;
using ClickLib;
using ClickLib.Bases;
using ClickLib.Clicks;
using DailyRoutines.Clicks;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.AddonLifecycle;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;

namespace DailyRoutines.Modules;

[ModuleDescription("AutoLeveQuestsTitle", "AutoLeveQuestsDescription", ModuleCategories.General)]
public class AutoLeveQuests : IDailyModule
{
    public bool Initialized { get; set; }

    private static Dictionary<uint, (string, uint)> LeveQuests = new();
    private static readonly HashSet<uint> QualifiedLeveCategories = new() { 9, 10, 11, 12, 13, 14, 15, 16 };

    private static (uint, string, uint)? SelectedLeve; // Leve ID - Leve Name - Leve Job Category

    private static TaskManager? TaskManager;

    private static uint LeveMeteDataId;
    private static uint LeveReceiverDataId;
    private static int Allowances;

    private static bool IsOnProcessing;

    public void Init()
    {
        TaskManager ??= new TaskManager { AbortOnTimeout = true, TimeLimitMS = 5000, ShowDebug = false };

        Initialized = true;
    }

    public void UI()
    {
        ImGui.BeginDisabled(IsOnProcessing);
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{Service.Lang.GetText("AutoLeveQuests-SelectedLeve")}");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(400f);
        if (ImGui.BeginCombo("##SelectedLeve",
                             SelectedLeve == null ? "" : $"{SelectedLeve.Value.Item1} | {SelectedLeve.Value.Item2}"))
        {
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.Button(Service.Lang.GetText("AutoLeveQuests-GetAreaLeveData"))) GetRecentLeveQuests();
            ImGui.Separator();

            foreach (var leveToSelect in LeveQuests)
            {
                if (ImGui.Selectable($"{leveToSelect.Key} | {leveToSelect.Value.Item1}"))
                    SelectedLeve = (leveToSelect.Key, leveToSelect.Value.Item1, leveToSelect.Value.Item2);
                if (SelectedLeve != null && ImGui.IsWindowAppearing() && SelectedLeve.Value.Item1 == leveToSelect.Key)
                    ImGui.SetScrollHereY();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(SelectedLeve == null || LeveMeteDataId == LeveReceiverDataId || LeveMeteDataId == 0 ||
                            LeveReceiverDataId == 0);
        if (ImGui.Button(Service.Lang.GetText("AutoLeveQuests-Start")))
        {
            IsOnProcessing = true;
            Service.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "Talk", SkipTalk);
            Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "JournalResult", StartAnotherRound);

            EnqueueSingleLeveQuest();
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(Service.Lang.GetText("AutoLeveQuests-Stop"))) EndProcessHandler();

        ImGui.BeginDisabled(IsOnProcessing);
        if (ImGui.Button(Service.Lang.GetText("AutoLeveQuests-ObtainLevemeteID"))) GetCurrentTargetDataID(out LeveMeteDataId);

        ImGui.SameLine();
        ImGui.Text(LeveMeteDataId.ToString());

        ImGui.SameLine();
        ImGui.Spacing();

        ImGui.SameLine();
        if (ImGui.Button(Service.Lang.GetText("AutoLeveQuests-ObtainLeveClientID"))) GetCurrentTargetDataID(out LeveReceiverDataId);

        ImGui.SameLine();
        ImGui.Text(LeveReceiverDataId.ToString());

        ImGui.EndDisabled();
    }

    private static void StartAnotherRound(AddonEvent eventType, AddonArgs addonInfo)
    {
        EnqueueSingleLeveQuest();
    }

    private static void EndProcessHandler()
    {
        TaskManager?.Abort();
        Service.AddonLifecycle.UnregisterListener(SkipTalk);
        Service.AddonLifecycle.UnregisterListener(StartAnotherRound);
        IsOnProcessing = false;
    }

    private static void SkipTalk(AddonEvent type, AddonArgs args)
    {
        if (EzThrottler.Throttle("AutoRetainerCollect-Talk", 100)) Click.SendClick("talk");
    }

    private static void EnqueueSingleLeveQuest()
    {
        // 和理符发行人交互
        TaskManager.Enqueue(InteractWithMete);
        // 点击制作任务
        TaskManager.Enqueue(() => Click.TrySendClick("select_string2"));
        // 点击接取任务
        TaskManager.Enqueue(ClickLeveQuest);
        // 退出理符任务界面
        TaskManager.Enqueue(ClickExit);
        // 退出 SelectString
        TaskManager.Enqueue(ClickSelectStringExit);
        // 和理符委托人交互
        TaskManager.Enqueue(InteractWithReceiver);
        // 选中任务
        TaskManager.Enqueue(ClickSelectQuest);
        // 确认提交任务
        TaskManager.Enqueue(ClickJournalResultConfirm);
    }

    private static void GetRecentLeveQuests()
    {
        var currentTerritoryPlaceNameId = Service.Data.GetExcelSheet<TerritoryType>()
                                                 .FirstOrDefault(y => y.RowId == Service.ClientState.TerritoryType)?
                                                 .PlaceName.RawRow.RowId;

        if (currentTerritoryPlaceNameId.HasValue)
        {
            LeveQuests = Service.Data.GetExcelSheet<Leve>()
                                .Where(x => !string.IsNullOrEmpty(x.Name.RawString) &&
                                            QualifiedLeveCategories.Contains(x.ClassJobCategory.RawRow.RowId) &&
                                            x.PlaceNameIssued.RawRow.RowId == currentTerritoryPlaceNameId.Value)
                                .ToDictionary(x => x.RowId, x => (x.Name.RawString, x.ClassJobCategory.RawRow.RowId));

            Service.Log.Debug($"Obtained {LeveQuests.Count} leve quests");
        }
    }

    private static void GetCurrentTargetDataID(out uint targetDataId)
    {
        var currentTarget = Service.Target.Target;
        targetDataId = currentTarget == null ? 0 : currentTarget.DataId;
    }

    private static unsafe bool? InteractWithMete()
    {
        if (Service.Condition[ConditionFlag.OccupiedInQuestEvent]) return false;
        if (FindObjectToInteractWith(LeveMeteDataId, out var foundObject))
        {
            TargetSystem.Instance()->InteractWithObject(foundObject);
            return true;
        }

        return false;
    }

    private static unsafe bool? InteractWithReceiver()
    {
        if (Service.Condition[ConditionFlag.OccupiedInQuestEvent]) return false;
        if (FindObjectToInteractWith(LeveReceiverDataId, out var foundObject))
        {
            TargetSystem.Instance()->InteractWithObject(foundObject);
            return true;
        }

        return false;
    }

    private static unsafe bool FindObjectToInteractWith(uint dataId, out GameObject* foundObject)
    {
        foreach (var obj in Service.ObjectTable.Where(o => o.DataId == dataId))
            if (obj.IsTargetable)
            {
                foundObject = (GameObject*)obj.Address;
                return true;
            }
        foundObject = null;
        return false;
    }

    private static unsafe bool? ClickLeveQuest()
    {
        if (SelectedLeve == null) return false;
        if (TryGetAddonByName<AddonGuildLeve>("GuildLeve", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(&addon->AtkUnitBase))
        {
            Allowances =
                int.TryParse(
                    addon->AtkComponentBase290->UldManager.NodeList[2]->GetAsAtkTextNode()->NodeText.ExtractText(),
                    out var result)
                    ? result
                    : 0;
            if (Allowances <= 0) EndProcessHandler();

            if (TryGetAddonByName<AddonJournalDetail>("JournalDetail", out var addon1) &&
                HelpersOm.IsAddonAndNodesReady(&addon1->AtkUnitBase))
            {
                var handler2 = new ClickJournalDetailDR();
                handler2.Accept((int)SelectedLeve.Value.Item1);

                return true;
            }
        }

        return false;
    }

    internal static unsafe bool? ClickExit()
    {
        if (TryGetAddonByName<AddonGuildLeve>("GuildLeve", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(&addon->AtkUnitBase))
        {
            var handler = new ClickGuildLeveDR();
            handler.Exit();

            return true;
        }

        return false;
    }

    private static unsafe bool? ClickSelectStringExit()
    {
        if (SelectedLeve == null) return false;
        if (TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(addon))
        {
            var handler = new ClickSelectString();
            handler.SelectItem4();

            return true;
        }

        return false;
    }

    private static unsafe bool? ClickSelectQuest()
    {
        if (SelectedLeve == null) return false;
        if (TryGetAddonByName<AddonSelectIconString>("SelectIconString", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(&addon->AtkUnitBase))
        {
            var i = 1;

            for (; i < 8; i++)
            {
                var text =
                    addon->PopupMenu.PopupMenu.List->AtkComponentBase.UldManager.NodeList[i]->GetAsAtkComponentNode()->
                        Component->UldManager.NodeList[4]->GetAsAtkTextNode()->NodeText.ExtractText();
                if (text == null) return false;
                if (text == SelectedLeve.Value.Item2) break;
            }

            var handler = new ClickSelectIconString();
            handler.SelectItem((ushort)(i - 1));

            return true;
        }

        return false;
    }

    private static unsafe bool? ClickJournalResultConfirm()
    {
        if (SelectedLeve == null) return false;
        if (TryGetAddonByName<AddonJournalResult>("JournalResult", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(&addon->AtkUnitBase))
        {
            var handler = new ClickJournalResult();
            var handler1 = new ClickJournalResultDR();
            handler.Complete();
            handler1.Exit();

            return true;
        }

        return false;
    }

    public void Uninit()
    {
        EndProcessHandler();

        Initialized = false;
    }
}
